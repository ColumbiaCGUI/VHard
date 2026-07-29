using System.Globalization;
using UnityEngine;

/// <summary>
/// Tracks headset presence for the running block: when the participant donned the headset,
/// how long they wore it, and the donning start the rehearsal clock measures latency from.
/// </summary>
public sealed class HeadsetPresenceTracker
{
    private readonly StudySessionState state;
    private readonly ActionRecorder actionRecorder;

    private float donningStartRealtime;
    private bool headsetPresenceInitialized;
    private bool headsetWasPresent;
    private float headsetPresentSinceRealtime = -1f;
    private bool blockHeadsetDonnedRecorded;
    private bool blockHeadsetWearActive;
    private float blockHeadsetWearSegmentStartRealtime;
    private float blockHeadsetWearSeconds;
    private bool headsetPresenceMismatchLogged;

    public HeadsetPresenceTracker(StudySessionState state, ActionRecorder actionRecorder)
    {
        this.state = state;
        this.actionRecorder = actionRecorder;
    }

    public float DonningStartRealtime => donningStartRealtime;
    public bool BlockHeadsetDonnedRecorded => blockHeadsetDonnedRecorded;

    public void UpdateHeadsetPresence()
    {
        bool present = OVRPlugin.userPresent;
        float now = Time.realtimeSinceStartup;
        if (!headsetPresenceInitialized)
        {
            headsetPresenceInitialized = true;
            headsetWasPresent = present;
            headsetPresentSinceRealtime = present ? now : -1f;
            return;
        }
        if (present == headsetWasPresent)
        {
            return;
        }

        headsetWasPresent = present;
        if (present)
        {
            headsetPresentSinceRealtime = now;
            BeginBlockHeadsetWear(now, "sensor_transition");
        }
        else
        {
            EndBlockHeadsetWear(now);
            headsetPresentSinceRealtime = -1f;
        }
    }

    public void InitializeBlockHeadsetWear()
    {
        blockHeadsetDonnedRecorded = state.activeRow != null && state.activeRow.condition == "A";
        blockHeadsetWearActive = false;
        blockHeadsetWearSegmentStartRealtime = 0f;
        blockHeadsetWearSeconds = 0f;
        headsetPresenceMismatchLogged = false;
        if (blockHeadsetDonnedRecorded)
        {
            donningStartRealtime = 0f;
            return;
        }

        UpdateHeadsetPresence();
        if (headsetWasPresent)
        {
            float blockWearStart = Time.realtimeSinceStartup;
            float donningStart = StudyRehearsalTiming.ResolveDonningStartRealtime(
                headsetPresentSinceRealtime,
                blockWearStart);
            BeginBlockHeadsetWear(blockWearStart, "present_at_block_start", donningStart);
        }
    }

    private void BeginBlockHeadsetWear(
        float wearStartedAt,
        string source,
        float donningStartedAt = -1f)
    {
        if (!state.blockRunning || state.activeRow == null || state.activeRow.condition == "A" ||
            blockHeadsetWearActive)
        {
            return;
        }

        blockHeadsetWearActive = true;
        blockHeadsetWearSegmentStartRealtime = wearStartedAt;
        string details = "block=" + state.activeRow.block.ToString(CultureInfo.InvariantCulture) +
                         ";source=" + source;
        if (!blockHeadsetDonnedRecorded)
        {
            blockHeadsetDonnedRecorded = true;
            donningStartRealtime = StudyRehearsalTiming.ResolveDonningStartRealtime(
                donningStartedAt,
                wearStartedAt);
            actionRecorder.Record("HeadsetDonned", state.activeRow.condition, null, details);
            return;
        }
        actionRecorder.Record("HeadsetRedonned", state.activeRow.condition, null, details);
    }

    private void EndBlockHeadsetWear(float removedAt)
    {
        if (!state.blockRunning || state.activeRow == null || state.activeRow.condition == "A" ||
            !blockHeadsetWearActive)
        {
            return;
        }

        float segmentSeconds = Mathf.Max(0f, removedAt - blockHeadsetWearSegmentStartRealtime);
        blockHeadsetWearSeconds += segmentSeconds;
        blockHeadsetWearActive = false;
        actionRecorder.Record(
            "HeadsetRemoved",
            state.activeRow.condition,
            null,
            "segmentSeconds=" + segmentSeconds.ToString("F3", CultureInfo.InvariantCulture) +
            ";wearSeconds=" + blockHeadsetWearSeconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    public void FinalizeBlockHeadsetWear()
    {
        if (state.activeRow == null || state.activeRow.condition == "A")
        {
            return;
        }

        UpdateHeadsetPresence();
        if (blockHeadsetWearActive)
        {
            float now = Time.realtimeSinceStartup;
            blockHeadsetWearSeconds += Mathf.Max(0f, now - blockHeadsetWearSegmentStartRealtime);
            blockHeadsetWearActive = false;
        }
        actionRecorder.Record(
            "HeadsetWearSummary",
            state.activeRow.condition,
            null,
            "wearSeconds=" + blockHeadsetWearSeconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    public void InferHeadsetDonnedFromInteraction(string interaction)
    {
        if (headsetPresenceMismatchLogged)
        {
            return;
        }

        headsetPresenceMismatchLogged = true;
        string details = "interaction=" + interaction + "; block=" +
                         state.activeRow.block.ToString(CultureInfo.InvariantCulture);
        actionRecorder.Record("HeadsetPresenceMismatch", state.activeRow.condition, null, details);
        Debug.LogWarning("[StudyManager] Interaction preceded the headset-presence signal; " +
                         "inferring donning. " + details);
        BeginBlockHeadsetWear(Time.realtimeSinceStartup, "inferred_from_interaction");
    }
}
