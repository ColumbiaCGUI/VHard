/// <summary>Why the palm-up console summon is standing down, so the recorded block rows name
/// the engagement that refused them.</summary>
public enum SummonBlockReason
{
    None,
    GripLatched,
    GripLocomotion,
    HoldHover,
    GhostProxyHeld,
}

public static class SummonBlockReasonExtensions
{
    public static string ToRecorderValue(this SummonBlockReason reason)
    {
        return reason switch
        {
            SummonBlockReason.GripLatched => "grip_latched",
            SummonBlockReason.GripLocomotion => "grip_locomotion",
            SummonBlockReason.HoldHover => "hold_hover",
            SummonBlockReason.GhostProxyHeld => "ghost_proxy_held",
            _ => string.Empty,
        };
    }
}

/// <summary>
/// Stands the palm-up console summon down while the participant is grip-engaged. The console is
/// the experimenter's panel, so there is no legitimate summon while a hand is on a hold - and a
/// hold gripped from beneath puts the palm exactly into the summon pose, so without this gate
/// every undercling reach dwells toward an accidental console open, and completing the dwell
/// suppresses gameplay input, which silently drops both hands' latches and releases any held
/// ghost proxy (P2/P3 sessions, 2026-08-18/19). Engagement is any of: a latched hand (any
/// acquisition path, wall hold or ghost proxy), active grip locomotion, either hand hovering a
/// hold (the hand-collider signal that precedes a grab), or a pinch-held ghost proxy. Hover
/// covers both hands because arming suppresses BOTH hands' acquisition, not just the summoning
/// hand's.
/// </summary>
public static class SummonGatePolicy
{
    public static SummonBlockReason GetBlockReason(
        bool leftLatched,
        bool rightLatched,
        bool gripLocomotionActive,
        bool leftHoveringHold,
        bool rightHoveringHold,
        bool ghostProxyHeld)
    {
        if (leftLatched || rightLatched)
        {
            return SummonBlockReason.GripLatched;
        }
        if (gripLocomotionActive)
        {
            return SummonBlockReason.GripLocomotion;
        }
        if (leftHoveringHold || rightHoveringHold)
        {
            return SummonBlockReason.HoldHover;
        }
        return ghostProxyHeld ? SummonBlockReason.GhostProxyHeld : SummonBlockReason.None;
    }
}
