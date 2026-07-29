using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;

public sealed class RecordingBlockSession
{
    public const int CaptureRate = 30;
    public const int QueueCapacity = CaptureRate * 2;
    public const float FlushIntervalSeconds = 5f;
    public const double CaptureIntervalSeconds = 1d / CaptureRate;

    private readonly string directory;
    private readonly double startRealtime;
    private readonly FixedRateCaptureTimer captureTimer;
    private readonly HoldAggregateCollection holdAggregates;
    private CaptureWriter captureWriter;
    private StreamWriter eventWriter;
    private ExceptionDispatchInfo sessionFailure;
    private double lastEventFlushRealtime;
    private bool stopRequested;
    private bool captureScheduleFinalized;
    private bool scheduledCapturePending;
    private bool ended;

    internal RecordingBlockSession(
        string directory,
        double startRealtime,
        StreamWriter eventWriter,
        CaptureWriter captureWriter)
        : this(
            directory,
            startRealtime,
            new FixedRateCaptureTimer(CaptureRate, startRealtime),
            new HoldAggregateCollection())
    {
        this.eventWriter = eventWriter;
        this.captureWriter = captureWriter;
    }

    private RecordingBlockSession(
        string directory,
        double startRealtime,
        FixedRateCaptureTimer captureTimer,
        HoldAggregateCollection holdAggregates)
    {
        this.directory = directory;
        this.startRealtime = startRealtime;
        this.captureTimer = captureTimer;
        this.holdAggregates = holdAggregates;
        lastEventFlushRealtime = startRealtime;
    }

    public string DirectoryPath => directory;
    public int DroppedCaptureFrames { get; private set; }
    public bool IsFinalized => ended;

    public static RecordingBlockSession Begin(
        string directory,
        bool recordToCsv,
        double startRealtime)
    {
        FixedRateCaptureTimer timer = new(CaptureRate, startRealtime);
        HoldAggregateCollection aggregates = new();
        RecordingBlockSession session = new(directory, startRealtime, timer, aggregates);
        bool eventFileCreated = false;
        bool captureFileCreated = false;
        FileStream eventFile = null;
        FileStream captureFile = null;

        try
        {
            Directory.CreateDirectory(directory);
            if (!recordToCsv)
            {
                return session;
            }

            string eventPath = Path.Combine(directory, "events.csv");
            eventFile = new FileStream(
                eventPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            eventFileCreated = true;
            session.eventWriter = new StreamWriter(eventFile, new UTF8Encoding(false));
            eventFile = null;
            session.eventWriter.WriteLine(RecordingCsvSerializer.EventHeader);
            session.eventWriter.Flush();

            string capturePath = Path.Combine(directory, "capture.csv.gz");
            captureFile = new FileStream(
                capturePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            captureFileCreated = true;
            session.captureWriter = CaptureWriter.Start(
                captureFile,
                QueueCapacity,
                TimeSpan.FromSeconds(FlushIntervalSeconds));
            captureFile = null;
            return session;
        }
        catch (Exception beginException)
        {
            List<Exception> rollbackErrors = new();
            TryRollback(() => session.eventWriter?.Dispose(), rollbackErrors);
            session.eventWriter = null;
            TryRollback(() => eventFile?.Dispose(), rollbackErrors);
            TryRollback(() => captureFile?.Dispose(), rollbackErrors);
            if (eventFileCreated)
            {
                TryRollback(() => File.Delete(Path.Combine(directory, "events.csv")), rollbackErrors);
            }
            if (captureFileCreated)
            {
                TryRollback(() => File.Delete(Path.Combine(directory, "capture.csv.gz")), rollbackErrors);
            }
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, beginException);
                throw new AggregateException(
                    "Recording block startup failed and rollback was incomplete.",
                    rollbackErrors);
            }
            throw;
        }
    }

    public bool TryScheduleCapture(double currentRealtime)
    {
        EnsureActive();
        if (scheduledCapturePending)
        {
            throw new InvalidOperationException(
                "The previous scheduled capture must be enqueued or marked dropped first.");
        }

        try
        {
            int elapsedIntervals = captureTimer.Advance(currentRealtime);
            if (elapsedIntervals == 0)
            {
                return false;
            }

            scheduledCapturePending = true;
            DroppedCaptureFrames = checked(
                DroppedCaptureFrames + elapsedIntervals - 1);
            return true;
        }
        catch (Exception exception)
        {
            CaptureFailure(exception);
            throw;
        }
    }

    public void DropScheduledCapture()
    {
        EnsureActive();
        if (!scheduledCapturePending)
        {
            throw new InvalidOperationException("No scheduled capture is pending.");
        }

        try
        {
            DroppedCaptureFrames = checked(DroppedCaptureFrames + 1);
            scheduledCapturePending = false;
        }
        catch (Exception exception)
        {
            CaptureFailure(exception);
            throw;
        }
    }

    public float GetBlockTime(double currentRealtime)
    {
        if (double.IsNaN(currentRealtime) ||
            double.IsInfinity(currentRealtime) ||
            currentRealtime < startRealtime)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRealtime));
        }
        return (float)(currentRealtime - startRealtime);
    }

    public void EnqueueCapture(
        CaptureFrame frame,
        string leftHold,
        float leftScore,
        string rightHold,
        float rightScore,
        bool sameTouchedHold)
    {
        EnsureActive();
        if (!scheduledCapturePending)
        {
            throw new InvalidOperationException("No scheduled capture is pending.");
        }

        bool captureAccepted = false;
        try
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            bool droppedOldest = captureWriter != null && captureWriter.Enqueue(frame);
            captureAccepted = true;
            scheduledCapturePending = false;
            holdAggregates.RecordTouches(
                leftHold,
                leftScore,
                rightHold,
                rightScore,
                sameTouchedHold,
                CaptureIntervalSeconds);
            if (droppedOldest)
            {
                DroppedCaptureFrames = checked(DroppedCaptureFrames + 1);
            }
        }
        catch (Exception exception)
        {
            if (!captureAccepted && scheduledCapturePending)
            {
                DroppedCaptureFrames = checked(DroppedCaptureFrames + 1);
                scheduledCapturePending = false;
            }
            CaptureFailure(exception);
            throw;
        }
    }

    public void WriteEvent(string line, string action, string hold)
    {
        EnsureActive();
        try
        {
            eventWriter?.WriteLine(line);
            if (action == "GripLatched" && !string.IsNullOrEmpty(hold))
            {
                holdAggregates.RecordGrip(hold);
            }
        }
        catch (Exception exception)
        {
            CaptureFailure(exception);
            throw;
        }
    }

    public void FlushEventsIfDue(double currentRealtime)
    {
        EnsureActive();
        if (eventWriter == null ||
            currentRealtime - lastEventFlushRealtime < FlushIntervalSeconds)
        {
            return;
        }

        try
        {
            eventWriter.Flush();
            lastEventFlushRealtime = currentRealtime;
        }
        catch (Exception exception)
        {
            CaptureFailure(exception);
            throw;
        }
    }

    public HoldAggregateData[] GetHoldAggregates()
    {
        return holdAggregates.ToArray();
    }

    public void RequestStop()
    {
        stopRequested = true;
        captureWriter?.RequestStop();
    }

    public void ReportFailure(Exception exception)
    {
        if (exception == null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        CaptureFailure(exception);
        RequestStop();
    }

    public void End(int writerTimeoutMilliseconds, double endRealtime)
    {
        if (ended)
        {
            ThrowIfFaulted();
            return;
        }

        List<Exception> errors = new();
        TimeoutException timeout = null;
        if (sessionFailure != null)
        {
            errors.Add(sessionFailure.SourceException);
        }

        if (!captureScheduleFinalized)
        {
            try
            {
                int pendingCapture = scheduledCapturePending ? 1 : 0;
                int trailingCaptures = captureTimer.Advance(endRealtime);
                DroppedCaptureFrames = checked(
                    DroppedCaptureFrames + pendingCapture + trailingCaptures);
            }
            catch (Exception exception)
            {
                AddUnique(errors, exception);
            }
            finally
            {
                scheduledCapturePending = false;
                captureScheduleFinalized = true;
            }
        }

        RequestStop();

        if (captureWriter != null)
        {
            try
            {
                captureWriter.StopAndFinalize(writerTimeoutMilliseconds);
            }
            catch (TimeoutException exception)
            {
                timeout = exception;
            }
            catch (Exception exception)
            {
                AddUnique(errors, exception);
            }
        }

        StreamWriter writer = eventWriter;
        eventWriter = null;
        if (writer != null)
        {
            try
            {
                writer.Flush();
            }
            catch (Exception exception)
            {
                AddUnique(errors, exception);
            }
            try
            {
                writer.Dispose();
            }
            catch (Exception exception)
            {
                AddUnique(errors, exception);
            }
        }

        ended = captureWriter == null || !captureWriter.IsAlive;
        if (timeout != null && captureWriter != null)
        {
            try
            {
                captureWriter.ThrowIfFaulted();
            }
            catch (Exception exception)
            {
                AddUnique(errors, exception);
            }
        }

        if (errors.Count == 0)
        {
            if (timeout != null)
            {
                ExceptionDispatchInfo.Capture(timeout).Throw();
            }
            return;
        }

        Exception report = errors.Count == 1
            ? errors[0]
            : new AggregateException("Recording block finalization failed.", errors);
        if (report.StackTrace == null)
        {
            try
            {
                throw report;
            }
            catch (Exception thrownException)
            {
                report = thrownException;
            }
        }
        sessionFailure = ExceptionDispatchInfo.Capture(report);
        if (timeout != null)
        {
            AggregateException attemptFailure = new(
                "Recording block finalization failed and the capture writer timed out.",
                report,
                timeout);
            throw attemptFailure;
        }
        sessionFailure.Throw();
    }

    public void ThrowIfFaulted()
    {
        sessionFailure?.Throw();
        captureWriter?.ThrowIfFaulted();
    }

    private void CaptureFailure(Exception exception)
    {
        sessionFailure ??= ExceptionDispatchInfo.Capture(exception);
    }

    private void EnsureActive()
    {
        ThrowIfFaulted();
        if (stopRequested || ended)
        {
            throw new InvalidOperationException("The recording block is finalizing or has ended.");
        }
    }

    private static void AddUnique(List<Exception> errors, Exception exception)
    {
        foreach (Exception existing in errors)
        {
            if (ReferenceEquals(existing, exception))
            {
                return;
            }
        }
        errors.Add(exception);
    }

    private static void TryRollback(Action action, List<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

}

internal sealed class HoldAggregateCollection
{
    private readonly Dictionary<string, HoldAggregateAccumulator> aggregates = new();

    public void RecordTouches(
        string leftHold,
        float leftScore,
        string rightHold,
        float rightScore,
        bool sameTouchedHold,
        double elapsedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) ||
            double.IsInfinity(elapsedSeconds) ||
            elapsedSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        if (leftHold != null)
        {
            float score = sameTouchedHold ? Math.Max(leftScore, rightScore) : leftScore;
            RecordTouch(leftHold, score, elapsedSeconds);
        }
        if (rightHold != null && !sameTouchedHold)
        {
            RecordTouch(rightHold, rightScore, elapsedSeconds);
        }
    }

    public void RecordGrip(string hold)
    {
        GetOrCreate(hold).gripsDetected++;
    }

    public HoldAggregateData[] ToArray()
    {
        HoldAggregateData[] result = new HoldAggregateData[aggregates.Count];
        int index = 0;
        foreach (KeyValuePair<string, HoldAggregateAccumulator> pair in aggregates)
        {
            HoldAggregateAccumulator aggregate = pair.Value;
            result[index++] = new HoldAggregateData
            {
                hold = pair.Key,
                secondsTouched = (float)aggregate.secondsTouched,
                gripsDetected = aggregate.gripsDetected,
                meanScore = aggregate.scoreSamples > 0
                    ? (float)(aggregate.scoreSum / aggregate.scoreSamples)
                    : -1f,
                maxScore = aggregate.scoreSamples > 0 ? aggregate.maxScore : -1f,
            };
        }
        Array.Sort(result, (left, right) => string.CompareOrdinal(left.hold, right.hold));
        return result;
    }

    private void RecordTouch(string hold, float score, double elapsedSeconds)
    {
        HoldAggregateAccumulator aggregate = GetOrCreate(hold);
        aggregate.secondsTouched += elapsedSeconds;
        if (score < 0f)
        {
            return;
        }

        aggregate.scoreSum += score;
        aggregate.scoreSamples++;
        aggregate.maxScore = Math.Max(aggregate.maxScore, score);
    }

    private HoldAggregateAccumulator GetOrCreate(string hold)
    {
        if (!aggregates.TryGetValue(hold, out HoldAggregateAccumulator aggregate))
        {
            aggregate = new HoldAggregateAccumulator();
            aggregates.Add(hold, aggregate);
        }
        return aggregate;
    }

    private sealed class HoldAggregateAccumulator
    {
        public double secondsTouched;
        public int gripsDetected;
        public double scoreSum;
        public int scoreSamples;
        public float maxScore;
    }
}
