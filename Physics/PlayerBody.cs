using System.Numerics;

namespace BlockCircuit;

/// <summary>Fixed-step player physics without input, rendering, or wall-clock dependencies.</summary>
public sealed class PlayerBody
{
    public const float Gravity = 20;
    public const float JumpSpeed = 7;
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

    public void Step(World world, Vector3 horizontalDirection, float moveSpeed, bool jumpPressed, float flyVertical, float dt)
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
            Grounded = verticalVelocity <= 0 && CollisionSystem.Collides(world, position - new Vector3(0, .002f, 0));
            if (Grounded && jumpPressed) { verticalVelocity = JumpSpeed; Grounded = false; }
            verticalVelocity = FiniteFloat((double)verticalVelocity - Gravity * (double)dt);
            verticalMovement = (double)verticalVelocity * dt;
        }
        var movement = new Vector3(
            FiniteFloat((double)horizontalDirection.X * moveSpeed * dt),
            FiniteFloat(verticalMovement),
            FiniteFloat((double)horizontalDirection.Z * moveSpeed * dt));
        var collision = CollisionSystem.Move(world, ref position, movement);
        if (collision.HitY) verticalVelocity = 0;
        Grounded = !Flying && movement.Y <= 0 &&
            (collision.Grounded || CollisionSystem.Collides(world, position - new Vector3(0, .002f, 0)));
        if (position.Y < -5)
        {
            position = world.Spawn();
            verticalVelocity = 0;
            Grounded = false;
        }
    }

    static float FiniteFloat(double value) => (float)Math.Clamp(value, -float.MaxValue, float.MaxValue);
}
