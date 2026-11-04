namespace BlockCircuit;

/// <summary>Input gestures independent of rendering and fixed simulation ticks.</summary>
public sealed class PlayerControls
{
    public const double DoubleTapSeconds = .3;
    double lastJump = double.NegativeInfinity, lastForward = double.NegativeInfinity;
    public bool Sprinting { get; private set; }

    public bool Sample(double now, bool jumpPressed, bool forwardPressed, bool forwardHeld, bool sprintHeld, bool sneak)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        bool toggleFlight = false;
        if (jumpPressed)
        {
            toggleFlight = now >= lastJump && now - lastJump <= DoubleTapSeconds;
            lastJump = toggleFlight ? double.NegativeInfinity : now;
        }
        if (forwardPressed)
        {
            if (now >= lastForward && now - lastForward <= DoubleTapSeconds) Sprinting = true;
            lastForward = now;
        }
        if (sprintHeld && forwardHeld) Sprinting = true;
        if (!forwardHeld || sneak) Sprinting = false;
        return toggleFlight;
    }

    public void Reset()
    {
        lastJump = lastForward = double.NegativeInfinity;
        Sprinting = false;
    }

    public static bool ShouldUseLever(Block target, bool sneaking) => target == Block.Lever && !sneaking;
    public static int ScrollSlot(int current, float wheel, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int direction = wheel > 0 ? -1 : wheel < 0 ? 1 : 0;
        return ((current + direction) % count + count) % count;
    }
}
