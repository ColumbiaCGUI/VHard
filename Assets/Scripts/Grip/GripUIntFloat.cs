using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit)]
internal struct GripUIntFloat
{
    [FieldOffset(0)] private uint unsigned;
    [FieldOffset(0)] private float floating;

    public static float ToFloat(uint value)
    {
        return new GripUIntFloat { unsigned = value }.floating;
    }

    public static uint ToUInt(float value)
    {
        return new GripUIntFloat { floating = value }.unsigned;
    }
}
