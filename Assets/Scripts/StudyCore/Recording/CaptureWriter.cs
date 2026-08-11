using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

[assembly: InternalsVisibleTo("VHard.EditModeTests")]

internal sealed class CaptureWriter
{
    private const int QueueWaitMilliseconds = 50;
    internal const int MaxChunkCharacters = 1024 * 1024;

    private readonly CaptureFrameQueue queue;
    private readonly Stream output;
    private readonly TimeSpan flushInterval;
    private readonly Thread thread;
    private ExceptionDispatchInfo writerFailure;
    private bool hasDurableCapture;

    private CaptureWriter(Stream output, int queueCapacity, TimeSpan flushInterval)
    {
        this.output = output;
        this.flushInterval = flushInterval;
        queue = new CaptureFrameQueue(queueCapacity);
        thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "VHard Capture Writer",
        };
    }

    public bool IsAlive => thread.IsAlive;
    public bool HasDurableCapture => Volatile.Read(ref hasDurableCapture);

    public static CaptureWriter Start(
        Stream output,
        int queueCapacity,
        TimeSpan flushInterval)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        if (!output.CanWrite)
        {
            throw new ArgumentException("Capture output must be writable.", nameof(output));
        }
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }
        if (flushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        CaptureWriter writer = new(output, queueCapacity, flushInterval);
        try
        {
            writer.thread.Start();
            return writer;
        }
        catch (Exception startException)
        {
            try
            {
                output.Dispose();
            }
            catch (Exception disposeException)
            {
                throw new AggregateException(
                    "The capture writer failed to start and its output failed to close.",
                    startException,
                    disposeException);
            }
            throw;
        }
    }

    public bool Enqueue(CaptureFrame frame)
    {
        ThrowIfFaulted();
        try
        {
            bool droppedOldest = queue.Enqueue(frame);
            ThrowIfFaulted();
            return droppedOldest;
        }
        catch (InvalidOperationException)
        {
            ThrowIfFaulted();
            throw;
        }
    }

    public void RequestStop()
    {
        queue.CompleteAdding();
    }

    public void StopAndFinalize(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        if (ReferenceEquals(Thread.CurrentThread, thread))
        {
            throw new InvalidOperationException("The capture writer cannot join its own thread.");
        }

        RequestStop();
        if (!thread.Join(timeoutMilliseconds))
        {
            throw new TimeoutException(
                $"Capture writer did not stop within {timeoutMilliseconds} milliseconds.");
        }
        ThrowIfFaulted();
    }

    public void ThrowIfFaulted()
    {
        ExceptionDispatchInfo failure = Volatile.Read(ref writerFailure);
        failure?.Throw();
        queue.ThrowIfFaulted();
    }

    private void WriterLoop()
    {
        Exception failure = null;
        try
        {
            WriteFrames();
        }
        catch (Exception exception)
        {
            failure = exception;
            PublishFailure(exception);
            queue.Fault(exception);
        }
        finally
        {
            try
            {
                output.Dispose();
            }
            catch (Exception disposeException)
            {
                failure = failure == null
                    ? disposeException
                    : new AggregateException(
                        "Capture writing and capture output disposal both failed.",
                        failure,
                        disposeException);
            }

            if (failure != null)
            {
                if (failure.StackTrace == null)
                {
                    try
                    {
                        throw failure;
                    }
                    catch (Exception thrownException)
                    {
                        failure = thrownException;
                    }
                }
                PublishFailure(failure);
                queue.Fault(failure);
            }
            queue.CompleteAdding();
        }
    }

    private void WriteFrames()
    {
        CaptureFrame writerFrame = new();
        StringBuilder chunk = new(MaxChunkCharacters);
        chunk.AppendLine(RecordingCsvSerializer.BuildCaptureHeader());
        long lastFlushTimestamp = Stopwatch.GetTimestamp();
        bool chunkContainsCapture = false;

        while (true)
        {
            CaptureQueueReadResult result = queue.Take(writerFrame, QueueWaitMilliseconds);
            if (result == CaptureQueueReadResult.Item)
            {
                RecordingCsvSerializer.AppendCaptureRow(chunk, writerFrame);
                chunkContainsCapture = true;
            }

            bool flushDue = GetElapsedSeconds(lastFlushTimestamp) >= flushInterval.TotalSeconds;
            bool sizeLimitReached = chunk.Length >= MaxChunkCharacters;
            bool firstCaptureMustBeDurable = chunkContainsCapture && !HasDurableCapture;
            if (chunk.Length > 0 &&
                (firstCaptureMustBeDurable || flushDue || sizeLimitReached ||
                 result == CaptureQueueReadResult.Completed))
            {
                WriteGzipMember(chunk);
                if (chunkContainsCapture)
                {
                    Volatile.Write(ref hasDurableCapture, true);
                }
                chunk.Clear();
                chunkContainsCapture = false;
                if (chunk.Capacity > MaxChunkCharacters)
                {
                    chunk.Capacity = MaxChunkCharacters;
                }
                lastFlushTimestamp = Stopwatch.GetTimestamp();
            }

            if (result == CaptureQueueReadResult.Completed)
            {
                return;
            }
        }
    }

    private void WriteGzipMember(StringBuilder chunk)
    {
        byte[] uncompressed = Encoding.UTF8.GetBytes(chunk.ToString());
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(
                   compressed,
                   CompressionLevel.Optimal,
                   true))
        {
            gzip.Write(uncompressed, 0, uncompressed.Length);
        }

        byte[] member = compressed.ToArray();
        output.Write(member, 0, member.Length);
        if (output is FileStream file)
        {
            file.Flush(true);
        }
        else
        {
            output.Flush();
        }
    }

    private void PublishFailure(Exception failure)
    {
        Volatile.Write(ref writerFailure, ExceptionDispatchInfo.Capture(failure));
    }

    private static double GetElapsedSeconds(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
    }
}

internal enum CaptureQueueReadResult
{
    TimedOut,
    Item,
    Completed,
}

internal sealed class CaptureFrameQueue
{
    private readonly object sync = new();
    private readonly CaptureFrame[] frames;
    private int readIndex;
    private int writeIndex;
    private int count;
    private bool addingCompleted;
    private ExceptionDispatchInfo queueFailure;

    public CaptureFrameQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        frames = new CaptureFrame[capacity];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = new CaptureFrame();
        }
    }

    public bool Enqueue(CaptureFrame source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        lock (sync)
        {
            queueFailure?.Throw();
            if (addingCompleted)
            {
                throw new InvalidOperationException("Cannot enqueue after capture finalization started.");
            }

            bool droppedOldest = count == frames.Length;
            if (droppedOldest)
            {
                readIndex = (readIndex + 1) % frames.Length;
                count--;
            }

            frames[writeIndex].CopyFrom(source);
            writeIndex = (writeIndex + 1) % frames.Length;
            count++;
            Monitor.Pulse(sync);
            return droppedOldest;
        }
    }

    public CaptureQueueReadResult Take(CaptureFrame destination, int timeoutMilliseconds)
    {
        lock (sync)
        {
            if (count == 0 && !addingCompleted)
            {
                Monitor.Wait(sync, timeoutMilliseconds);
            }

            if (count > 0)
            {
                destination.CopyFrom(frames[readIndex]);
                readIndex = (readIndex + 1) % frames.Length;
                count--;
                return CaptureQueueReadResult.Item;
            }
            return addingCompleted
                ? CaptureQueueReadResult.Completed
                : CaptureQueueReadResult.TimedOut;
        }
    }

    public void CompleteAdding()
    {
        lock (sync)
        {
            addingCompleted = true;
            Monitor.PulseAll(sync);
        }
    }

    public void Fault(Exception exception)
    {
        if (exception == null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        ExceptionDispatchInfo failure = ExceptionDispatchInfo.Capture(exception);
        lock (sync)
        {
            queueFailure ??= failure;
            addingCompleted = true;
            Monitor.PulseAll(sync);
        }
    }

    public void ThrowIfFaulted()
    {
        ExceptionDispatchInfo failure;
        lock (sync)
        {
            failure = queueFailure;
        }
        failure?.Throw();
    }
}
