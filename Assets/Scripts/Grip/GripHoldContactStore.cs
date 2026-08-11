using System.Collections.Generic;
using UnityEngine;

internal sealed class GripHoldContactStore
{
    private readonly GripContactReadbackProcessor readback;
    private readonly GripScoreConfig config;
    private readonly Dictionary<int, GripHoldContactState> holdStates = new();
    private readonly List<int> staleStateIds = new();

    public GripHoldContactStore(GripContactReadbackProcessor readback, GripScoreConfig config)
    {
        this.readback = readback;
        this.config = config;
    }

    public void Retain(IReadOnlyList<GameObject> holds)
    {
        HashSet<int> retainedIds = new();
        if (holds != null)
        {
            foreach (GameObject hold in holds)
            {
                if (hold != null)
                {
                    retainedIds.Add(hold.GetInstanceID());
                }
            }
        }

        staleStateIds.Clear();
        foreach (KeyValuePair<int, GripHoldContactState> pair in holdStates)
        {
            if (!retainedIds.Contains(pair.Key))
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }
    }

    public void Prepare(GameObject hold)
    {
        if (hold == null || holdStates.ContainsKey(hold.GetInstanceID()) ||
            !hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return;
        }

        holdStates.Add(
            hold.GetInstanceID(),
            new GripHoldContactState(readback, config, hold, meshFilter, config.contactPatchMaterial));
    }

    public GripHoldContactState ResolveState(GameObject hold)
    {
        if (!hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return null;
        }

        int id = hold.GetInstanceID();
        if (!holdStates.TryGetValue(id, out GripHoldContactState state))
        {
            Prepare(hold);
            state = holdStates[id];
        }
        return state;
    }

    public void HideAllOverlays()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.SetOverlayVisible(false);
        }
    }

    public void InvalidateAllContactData()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.InvalidateContactData();
        }
    }

    public void InvalidateHoldContact(GameObject hold)
    {
        if (hold != null && holdStates.TryGetValue(hold.GetInstanceID(), out GripHoldContactState state))
        {
            state.InvalidateContactData();
        }
    }

    public void SetLatchFeedback(GameObject hold, int handMask, bool latched)
    {
        if (hold == null)
        {
            return;
        }

        GripHoldContactState state;
        if (latched)
        {
            state = ResolveState(hold);
        }
        else
        {
            holdStates.TryGetValue(hold.GetInstanceID(), out state);
        }
        state?.SetLatchedHand(handMask, latched);
    }

    public void ClearAllLatchFeedback()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.ClearLatchFeedback();
        }
    }

    public void RemoveDestroyedStates()
    {
        staleStateIds.Clear();
        foreach (KeyValuePair<int, GripHoldContactState> pair in holdStates)
        {
            if (pair.Value.hold == null)
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }
    }

    public void DisposeAll()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.Dispose();
        }
        holdStates.Clear();
    }
}
