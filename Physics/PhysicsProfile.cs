namespace BlockCircuit;

/// <summary>BlockCircuit's Java-style, dry-land movement profile. Units are blocks and seconds.</summary>
public static class PhysicsProfile
{
    public const int TickRate = 20;
    public const float TickSeconds = 1f / TickRate;
    public const float GravityPerTick = .08f;
    public const float JumpPerTick = .42f;
    public const float VerticalDrag = .98f;
    public const float AirDrag = .91f;
    public const float DefaultSlipperiness = .6f;
    public const float GroundDrag = DefaultSlipperiness * AirDrag;
    // 0.1 ground acceleration * 0.98 input damping, with steady pre-drag displacement.
    public const float WalkSpeed = .098f / (1f - GroundDrag) * TickRate;
    public const float SprintSpeed = WalkSpeed * 1.3f;
    public const float SneakSpeed = WalkSpeed * .3f;
    public const float AirAcceleration = .02f * TickRate;
    public const float JumpRepeatSeconds = .5f;
}
