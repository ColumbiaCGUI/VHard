using NUnit.Framework;

public sealed class SummonGatePolicyTests
{
    [Test]
    public void FreeHandsAllowTheSummon()
    {
        Assert.AreEqual(
            SummonBlockReason.None,
            SummonGatePolicy.GetBlockReason(false, false, false, false, false, false));
    }

    [Test]
    public void EitherLatchedHandBlocks()
    {
        Assert.AreEqual(
            SummonBlockReason.GripLatched,
            SummonGatePolicy.GetBlockReason(true, false, false, false, false, false));
        Assert.AreEqual(
            SummonBlockReason.GripLatched,
            SummonGatePolicy.GetBlockReason(false, true, false, false, false, false));
    }

    [Test]
    public void GripLocomotionBlocks()
    {
        Assert.AreEqual(
            SummonBlockReason.GripLocomotion,
            SummonGatePolicy.GetBlockReason(false, false, true, false, false, false));
    }

    [Test]
    public void EitherHandHoveringAHoldBlocks()
    {
        // The right hand blocks too: arming suppresses BOTH hands' acquisition, so a right-hand
        // reach during a left palm-up must stand the summon down before it can disarm the grab.
        Assert.AreEqual(
            SummonBlockReason.HoldHover,
            SummonGatePolicy.GetBlockReason(false, false, false, true, false, false));
        Assert.AreEqual(
            SummonBlockReason.HoldHover,
            SummonGatePolicy.GetBlockReason(false, false, false, false, true, false));
    }

    [Test]
    public void HeldGhostProxyBlocks()
    {
        Assert.AreEqual(
            SummonBlockReason.GhostProxyHeld,
            SummonGatePolicy.GetBlockReason(false, false, false, false, false, true));
    }

    [Test]
    public void LatchOutranksTheOtherReasonsInTheRecordedRow()
    {
        Assert.AreEqual(
            SummonBlockReason.GripLatched,
            SummonGatePolicy.GetBlockReason(true, false, true, true, true, true));
        Assert.AreEqual(
            SummonBlockReason.GripLocomotion,
            SummonGatePolicy.GetBlockReason(false, false, true, true, true, true));
        Assert.AreEqual(
            SummonBlockReason.HoldHover,
            SummonGatePolicy.GetBlockReason(false, false, false, true, true, true));
    }

    [Test]
    public void RecorderValuesNameEveryReason()
    {
        Assert.AreEqual(string.Empty, SummonBlockReason.None.ToRecorderValue());
        Assert.AreEqual("grip_latched", SummonBlockReason.GripLatched.ToRecorderValue());
        Assert.AreEqual("grip_locomotion", SummonBlockReason.GripLocomotion.ToRecorderValue());
        Assert.AreEqual("hold_hover", SummonBlockReason.HoldHover.ToRecorderValue());
        Assert.AreEqual("ghost_proxy_held", SummonBlockReason.GhostProxyHeld.ToRecorderValue());
    }
}
