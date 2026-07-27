public enum GripReadbackAction
{
    None,
    Recover,
    Degrade,
}

public sealed class GripReadbackEpochState
{
    private bool statisticsCompleted;
    private bool statisticsSucceeded;
    private bool bonesCompleted;
    private bool bonesSucceeded;

    public bool IsComplete => statisticsCompleted && bonesCompleted;
    public bool Succeeded => IsComplete && statisticsSucceeded && bonesSucceeded;

    public void Reset()
    {
        statisticsCompleted = false;
        statisticsSucceeded = false;
        bonesCompleted = false;
        bonesSucceeded = false;
    }

    public void RecordStatistics(bool succeeded)
    {
        statisticsSucceeded = succeeded;
        statisticsCompleted = true;
    }

    public void RecordBones(bool succeeded)
    {
        bonesSucceeded = succeeded;
        bonesCompleted = true;
    }
}

/// <summary>
/// Ordered epoch health accounting for the grip GPU pipeline. An epoch result is reported
/// only after both of its readbacks finish, so isolated or half-complete readbacks cannot
/// advance the failure threshold.
/// </summary>
public sealed class GripReadbackHealth
{
    public const int FailureThreshold = 15;
    public const float MinimumSecondsSinceSuccess = 1f;

    private readonly bool recoveryAttempted;
    private bool actionIssued;

    public int ConsecutiveFailures { get; private set; }
    public float LastSuccessTime { get; private set; }

    public GripReadbackHealth(bool recoveryAttempted, float startedAt)
    {
        this.recoveryAttempted = recoveryAttempted;
        LastSuccessTime = startedAt;
    }

    public GripReadbackAction RecordEpoch(bool succeeded, float now)
    {
        if (actionIssued)
        {
            return GripReadbackAction.None;
        }

        if (succeeded)
        {
            ConsecutiveFailures = 0;
            LastSuccessTime = now;
        }
        else
        {
            ConsecutiveFailures++;
        }

        return Evaluate(now);
    }

    public GripReadbackAction Evaluate(float now)
    {
        if (actionIssued || ConsecutiveFailures < FailureThreshold ||
            (!recoveryAttempted && now - LastSuccessTime < MinimumSecondsSinceSuccess))
        {
            return GripReadbackAction.None;
        }

        actionIssued = true;
        return recoveryAttempted ? GripReadbackAction.Degrade : GripReadbackAction.Recover;
    }
}
