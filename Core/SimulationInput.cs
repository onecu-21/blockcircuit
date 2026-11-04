using System.Numerics;

namespace BlockCircuit;

public readonly record struct MovementInput(Vector3 Direction, float Speed, bool Jump, float FlyVertical, bool Sneak, bool ToggleFlight = false);

/// <summary>Retains a short frame's jump press until a simulation tick consumes it.</summary>
public sealed class SimulationInput
{
    MovementInput held;
    bool jumpPending;
    public bool FlightTogglePending { get; private set; }

    public void QueueFlightToggle() => FlightTogglePending = !FlightTogglePending;

    public void Sample(Vector3 direction, float speed, bool jumpPressed, bool jumpHeld, float flyVertical, bool sneak)
    {
        CollisionSystem.RequireFinite(direction, nameof(direction));
        if (!float.IsFinite(speed) || speed < 0) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!float.IsFinite(flyVertical)) throw new ArgumentOutOfRangeException(nameof(flyVertical));
        held = new(direction, speed, jumpHeld, flyVertical, sneak);
        jumpPending |= jumpPressed;
    }

    public MovementInput Consume()
    {
        var result = held with { Jump = held.Jump || jumpPending, ToggleFlight = FlightTogglePending };
        FlightTogglePending = false;
        jumpPending = false;
        return result;
    }

    public void Reset() { held = default; jumpPending = false; FlightTogglePending = false; }
}
