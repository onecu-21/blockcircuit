using System.Numerics;

namespace BlockCircuit;

/// <summary>Fixed-step player physics without input, rendering, or wall-clock dependencies.</summary>
public sealed class PlayerBody
{
    public const float Gravity = 20;
    public const float JumpSpeed = 7;
    public const float CrouchHeight = 1.5f;
    Vector3 position;
    float verticalVelocity;
    bool flying;

    public Vector3 Position
    {
        get => position;
        set { CollisionSystem.RequireFinite(value, nameof(Position)); position = value; Grounded = false; }
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

        // Old saves allow a slightly taller flight range; bring them into current bounds.
        position.X = Math.Clamp(position.X, CollisionSystem.BoundaryMargin, World.Size - CollisionSystem.BoundaryMargin);
        position.Z = Math.Clamp(position.Z, CollisionSystem.BoundaryMargin, World.Size - CollisionSystem.BoundaryMargin);
        position.Y = Math.Min(position.Y, CollisionSystem.MaximumFeetY);
        if (sneak && !Flying) Crouching = true;
        else if (!CollisionSystem.Collides(world, position, CollisionSystem.BodyHeight)) Crouching = false;
        double length = Math.Sqrt((double)horizontalDirection.X * horizontalDirection.X + (double)horizontalDirection.Z * horizontalDirection.Z);
        if (length > 1)
            horizontalDirection = new((float)(horizontalDirection.X / length), 0, (float)(horizontalDirection.Z / length));

        double verticalMovement;
        if (Flying)
        {
            verticalVelocity = 0;
            Grounded = false;
            verticalMovement = (double)Math.Clamp(flyVertical, -1, 1) * moveSpeed * dt;
        }
        else
        {
            // A short support query also permits jumping immediately after a teleport to a floor.
            Grounded = verticalVelocity <= 0 && CollisionSystem.HasSupport(world, position);
            if (Grounded && jumpPressed) { verticalVelocity = JumpSpeed; Grounded = false; }
            verticalVelocity = FiniteFloat((double)verticalVelocity - Gravity * (double)dt);
            verticalMovement = (double)verticalVelocity * dt;
        }
        var movement = new Vector3(
            FiniteFloat((double)horizontalDirection.X * moveSpeed * dt),
            FiniteFloat(verticalMovement),
            FiniteFloat((double)horizontalDirection.Z * moveSpeed * dt));
        if (Crouching && Grounded && !Flying && !jumpPressed)
        {
            MoveAlongSupportedGround(world, movement.X, movement.Z);
            movement.X = movement.Z = 0;
        }
        var collision = CollisionSystem.Move(world, ref position, movement, BodyHeight);
        if (collision.HitY) verticalVelocity = 0;
        Grounded = !Flying && movement.Y <= 0 &&
            (collision.Grounded || CollisionSystem.HasSupport(world, position));
        if (position.Y < -5)
        {
            position = world.Spawn();
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
