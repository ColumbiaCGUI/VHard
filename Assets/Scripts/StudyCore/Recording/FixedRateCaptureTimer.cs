using System;

internal sealed class FixedRateCaptureTimer
{
    private readonly double intervalSeconds;
    private double lastAdvanceTime;
    private double intervalAccumulator;

    public FixedRateCaptureTimer(int captureRate, double startTime)
    {
        if (captureRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureRate));
        }
        if (double.IsNaN(startTime) || double.IsInfinity(startTime))
        {
            throw new ArgumentOutOfRangeException(nameof(startTime));
        }

        intervalSeconds = 1d / captureRate;
        lastAdvanceTime = startTime;
    }

    public int Advance(double currentTime)
    {
        if (double.IsNaN(currentTime) || double.IsInfinity(currentTime) || currentTime < lastAdvanceTime)
        {
            throw new ArgumentOutOfRangeException(nameof(currentTime),
                "Capture time must be finite and monotonic.");
        }

        intervalAccumulator += currentTime - lastAdvanceTime;
        lastAdvanceTime = currentTime;
        long elapsedIntervals = (long)Math.Floor(
            (intervalAccumulator + intervalSeconds * 1e-9d) / intervalSeconds);
        if (elapsedIntervals == 0)
        {
            return 0;
        }

        intervalAccumulator -= elapsedIntervals * intervalSeconds;
        if (intervalAccumulator < 0d)
        {
            intervalAccumulator = 0d;
        }

        return checked((int)elapsedIntervals);
    }
}
