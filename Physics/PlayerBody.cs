using System.Numerics;

namespace BlockCircuit;

/// <summary>Fixed-step player physics without input, rendering, or wall-clock dependencies.</summary>
public sealed class PlayerBody
{
    public const float Gravity = PhysicsProfile.GravityPerTick * PhysicsProfile.TickRate * PhysicsProfile.TickRate;
    public const float JumpSpeed = PhysicsProfile.JumpPerTick * PhysicsProfile.TickRate;
    public const float CrouchHeight = 1.5f;
    Vector3 position;
    float verticalVelocity;
    bool flying;
    Vector3 horizontalVelocity;
    float jumpCooldown;

    /// <summary>Post-drag horizontal velocity in blocks/second.</summary>
    public Vector3 HorizontalVelocity => horizontalVelocity;

    public Vector3 Position
    {
        get => position;
        set { CollisionSystem.RequireFinite(value, nameof(Position)); position = value; verticalVelocity = 0; horizontalVelocity = Vector3.Zero; jumpCooldown = 0; Grounded = false; }
    }

    public float VerticalVelocity
    {
        get => verticalVelocity;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(VerticalVelocity));
            verticalVelocity = value;
        }
    }

    public bool Grounded { get; private set; }
    public bool Crouching { get; private set; }
    public float BodyHeight => Crouching ? CrouchHeight : CollisionSystem.BodyHeight;
    public float EyeHeight => Crouching ? 1.27f : 1.62f;
    public bool Flying
    {
        get => flying;
        set
        {
            if (flying == value) return;
            flying = value;
            verticalVelocity = 0;
            horizontalVelocity = Vector3.Zero;
            jumpCooldown = 0;
            Grounded = false;
        }
    }

    public void Step(World world, Vector3 horizontalDirection, float moveSpeed, bool jumpPressed, float flyVertical, float dt, bool sneak = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        CollisionSystem.RequireFinite(horizontalDirection, nameof(horizontalDirection));
        if (!float.IsFinite(moveSpeed) || moveSpeed < 0) throw new ArgumentOutOfRangeException(nameof(moveSpeed));
        if (!float.IsFinite(flyVertical)) throw new ArgumentOutOfRangeException(nameof(flyVertical));
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        // Normal simulation calls once at 50 ms. Bound compatibility substeps for tools
        // that pass longer durations, while retaining swept collision for huge inputs.
        int steps = (int)Math.Min(8, Math.Ceiling((double)dt / PhysicsProfile.TickSeconds));
        for (int i = 0; i < steps; i++)
            StepMovement(world, horizontalDirection, moveSpeed, jumpPressed, flyVertical, dt / steps, sneak);
    }

    void StepMovement(World world, Vector3 horizontalDirection, float moveSpeed, bool jumpPressed, float flyVertical, float dt, bool sneak)
    {

        // Old saves allow a slightly taller flight range; bring them into current bounds.
        position.X = Math.Clamp(position.X, CollisionSystem.BoundaryMargin, World.Size - CollisionSystem.BoundaryMargin);
        position.Z = Math.Clamp(position.Z, CollisionSystem.BoundaryMargin, World.Size - CollisionSystem.BoundaryMargin);
        position.Y = Math.Min(position.Y, CollisionSystem.MaximumFeetY);
        if (sneak && !Flying) Crouching = true;
        else if (!CollisionSystem.Collides(world, position, CollisionSystem.BodyHeight)) Crouching = false;
        double length = Math.Sqrt((double)horizontalDirection.X * horizontalDirection.X + (double)horizontalDirection.Z * horizontalDirection.Z);
        if (length > 1)
            horizontalDirection = new((float)(horizontalDirection.X / length), 0, (float)(horizontalDirection.Z / length));

        double tickFraction = (double)dt / PhysicsProfile.TickSeconds;
        if (!jumpPressed) jumpCooldown = 0;
        else jumpCooldown = Math.Max(0, jumpCooldown - dt);
        Grounded = !Flying && verticalVelocity <= 0 && CollisionSystem.HasSupport(world, position);
        bool groundMovement = Grounded;
        if (Grounded && jumpPressed && jumpCooldown <= 0)
        {
            verticalVelocity = JumpSpeed;
            jumpCooldown = PhysicsProfile.JumpRepeatSeconds;
            Grounded = false;
        }

        Vector3 movement;
        if (Flying)
        {
            verticalVelocity = 0;
            horizontalVelocity = Vector3.Zero;
            movement = new(FiniteFloat((double)horizontalDirection.X * moveSpeed * dt),
                FiniteFloat((double)Math.Clamp(flyVertical, -1, 1) * moveSpeed * dt),
                FiniteFloat((double)horizontalDirection.Z * moveSpeed * dt));
        }
        else
        {
            // Accelerate, move, then damp. On ordinary ground the steady pre-drag
            // displacement is moveSpeed / 20; releasing input retains momentum.
            float drag = groundMovement ? PhysicsProfile.GroundDrag : PhysicsProfile.AirDrag;
            double acceleration = groundMovement ? moveSpeed * (1d - drag) :
                PhysicsProfile.AirAcceleration * Math.Clamp(moveSpeed / PhysicsProfile.WalkSpeed, 0, 1.3f);
            double vx = horizontalVelocity.X + horizontalDirection.X * acceleration * tickFraction;
            double vz = horizontalVelocity.Z + horizontalDirection.Z * acceleration * tickFraction;
            movement = new(FiniteFloat(vx * dt), FiniteFloat((double)verticalVelocity * dt), FiniteFloat(vz * dt));
            double damping = Math.Pow(drag, tickFraction);
            horizontalVelocity = new(FiniteFloat(vx * damping), 0, FiniteFloat(vz * damping));
        }
        if (Crouching && Grounded && !Flying && !jumpPressed)
        {
            var beforeSneak = position;
            MoveAlongSupportedGround(world, movement.X, movement.Z);
            if (Math.Abs(position.X - beforeSneak.X - movement.X) > .0001f) horizontalVelocity.X = 0;
            if (Math.Abs(position.Z - beforeSneak.Z - movement.Z) > .0001f) horizontalVelocity.Z = 0;
            movement.X = movement.Z = 0;
        }
        var collision = CollisionSystem.Move(world, ref position, movement, BodyHeight);
        if (collision.HitX) horizontalVelocity.X = 0;
        if (collision.HitZ) horizontalVelocity.Z = 0;
        if (collision.HitY) verticalVelocity = 0;
        if (!Flying)
        {
            // Exact 20 TPS recurrence: vNext = (v - 0.08 * 20) * 0.98.
            double drag = PhysicsProfile.VerticalDrag;
            double damping = Math.Pow(drag, tickFraction);
            double terminal = Gravity * PhysicsProfile.TickSeconds * drag / (1 - drag);
            verticalVelocity = FiniteFloat((verticalVelocity + terminal) * damping - terminal);
        }
        Grounded = !Flying && movement.Y <= 0 &&
            (collision.Grounded || CollisionSystem.HasSupport(world, position));
        if (position.Y < -5)
        {
            position = world.Spawn();
            horizontalVelocity = Vector3.Zero;
            jumpCooldown = 0;
            verticalVelocity = 0;
            Grounded = false;
            Crouching = false;
        }
    }

    void MoveAlongSupportedGround(World world, float moveX, float moveZ)
    {
        // Follow short, collision-clipped steps so diagonal motion cannot cut a corner
        // or skip over an unsupported gap to another platform in a long frame.
        float dx = Math.Clamp(moveX, -World.Size, World.Size);
        float dz = Math.Clamp(moveZ, -World.Size, World.Size);
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dz)) / .05));
        var stepX = new Vector3(dx / steps, 0, 0);
        var stepZ = new Vector3(0, 0, dz / steps);
        for (int i = 0; i < steps; i++)
        {
            var candidate = position;
            CollisionSystem.Move(world, ref candidate, stepX, BodyHeight);
            if (CollisionSystem.HasSupport(world, candidate)) position = candidate;
            candidate = position;
            CollisionSystem.Move(world, ref candidate, stepZ, BodyHeight);
            if (CollisionSystem.HasSupport(world, candidate)) position = candidate;
        }
    }

    static float FiniteFloat(double value) => (float)Math.Clamp(value, -float.MaxValue, float.MaxValue);
}
