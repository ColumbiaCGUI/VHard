using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Console labelling for the approved study routes. Every route reference the experimenter
/// console renders is produced here and carries the derived code alone: the MoonBoard id,
/// community grade, problem name, and hold list must never reach a panel a participant can see.
/// Diagnostics that do name the catalog record belong in the Unity log and the recorder.
/// </summary>
public static class StudyRouteIdentity
{
    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int CodeLength = 4;

    /// <summary>
    /// FNV-1a over the invariant upper-case route id, rendered in a base-32 alphabet without
    /// characters that read alike in the headset. The code depends on the id alone, so it
    /// survives catalog reordering and can be printed onto an experimenter card from the same
    /// route ids the schedule uses.
    /// </summary>
    public static string GetRouteCode(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            throw new ArgumentException("A route id is required to derive a console code.", nameof(routeId));
        }

        uint hash = 2166136261u;
        foreach (char character in routeId.Trim().ToUpperInvariant())
        {
            hash = (hash ^ character) * 16777619u;
        }

        char[] code = new char[CodeLength];
        for (int index = CodeLength - 1; index >= 0; index--)
        {
            code[index] = CodeAlphabet[(int)(hash % (uint)CodeAlphabet.Length)];
            hash /= (uint)CodeAlphabet.Length;
        }
        return new string(code);
    }

    public static string FormatSlot(int routeIndex, int routeCount)
    {
        if (routeCount <= 0)
        {
            return "NO ROUTES";
        }

        int slot = Math.Clamp(routeIndex, 0, routeCount - 1) + 1;
        return "SLOT " + slot.ToString(CultureInfo.InvariantCulture) + " / " +
               routeCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The console's only way to name a route record. Panel and session code render route
    /// references through this method so that no call site can reach for the catalog id.
    /// </summary>
    public static string FormatCodeReference(string routeId)
    {
        return string.IsNullOrWhiteSpace(routeId) ? "NO ROUTE" : "CODE " + GetRouteCode(routeId);
    }

    /// <summary>Slot and code only: the readout the console leaves on screen.</summary>
    public static string FormatBlindLabel(string routeId, int routeIndex, int routeCount)
    {
        return routeCount <= 0 || string.IsNullOrWhiteSpace(routeId)
            ? "NO ROUTES"
            : FormatSlot(routeIndex, routeCount) + "     " + FormatCodeReference(routeId);
    }

    /// <summary>Blind label of the route <paramref name="offset"/> steps along the wrapped list,
    /// so the previous and next buttons name their destination before they are pressed.</summary>
    public static string FormatStepLabel(IReadOnlyList<string> routeIds, int routeIndex, int offset)
    {
        if (routeIds == null || routeIds.Count == 0)
        {
            return "NO ROUTES";
        }

        int count = routeIds.Count;
        int stepped = ((Math.Clamp(routeIndex, 0, count - 1) + offset) % count + count) % count;
        string slot = (stepped + 1).ToString(CultureInfo.InvariantCulture);
        string code = GetRouteCode(routeIds[stepped]);
        return offset < 0 ? "< " + slot + "  " + code : slot + "  " + code + " >";
    }

    /// <summary>
    /// Console status for a route that failed to load or validate. The underlying diagnostic
    /// names the catalog record and its holds, so it goes to the log while the console names the
    /// route by code alone.
    /// </summary>
    public static string FormatRouteFailureStatus(string routeId)
    {
        return string.IsNullOrWhiteSpace(routeId)
            ? "The selected route is unavailable; see the log."
            : FormatCodeReference(routeId) + " is unavailable; see the log.";
    }
}
