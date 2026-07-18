using System;

[Serializable]
public sealed class StudyScheduleRow
{
    public string participant;
    public int block;
    public string condition;
    public string route;
}

[Serializable]
public sealed class HoldAggregateData
{
    public string hold;
    public float secondsTouched;
    public int gripsDetected;
    public float meanScore;
    public float maxScore;
}

[Serializable]
public sealed class StudySessionManifest
{
    public string participant;
    public int block;
    public string condition;
    public string route;
    public int retry;
    public bool adhoc;
    public string appVersion;
    public string gitRevision;
    public string startUtc;
    public string endUtc;
    public bool endedEarly;
    public string endReason;
    public string routesJsonSha256;
    public int droppedCaptureFrames;
    public HoldAggregateData[] holdAggregates = Array.Empty<HoldAggregateData>();
}
