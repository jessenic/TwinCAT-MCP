namespace TwinCatMcp.Runtime;

/// <summary>
/// Decodes the NC axis status double word (<c>AXIS_REF.NcToPlc.StateDWord</c>, Tc2_MC2) into named flags.
/// Only the documented, commonly used bits are named; the raw value is always reported alongside.
/// </summary>
public static class AxisStateDecoder
{
    private static readonly (int Bit, string Name)[] Bits =
    [
        (0, "Operational"),
        (1, "Homed"),
        (2, "NotMoving"),
        (3, "InPositionArea"),
        (4, "InTargetPosition"),
        (5, "Protected"),
        (7, "HasBeenStopped"),
        (8, "HasJob"),
        (9, "PositiveDirection"),
        (10, "NegativeDirection"),
        (11, "HomingBusy"),
        (12, "ConstantVelocity"),
        (26, "SoftLimitMinExceeded"),
        (27, "SoftLimitMaxExceeded"),
        (28, "DriveDeviceError"),
        (29, "MotionCommandsLocked"),
        (30, "IoDataInvalid"),
        (31, "Error"),
    ];

    public static IReadOnlyList<string> Decode(uint stateDWord)
        => Bits.Where(b => (stateDWord & (1u << b.Bit)) != 0).Select(b => b.Name).ToArray();
}
