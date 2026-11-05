using System.Numerics;

namespace BlockCircuit;

public readonly record struct CollisionResult(bool HitX, bool HitY, bool HitZ, bool Grounded);

/// <summary>Pure voxel collision queries and swept, axis-separated player movement.</summary>
public static class CollisionSystem
{
    public const float HalfWidth = .29f;
    public const float BodyHeight = 1.75f;
    public const float BoundaryMargin = .3f;
    public const float MaximumFeetY = World.Height + 8;
    public const float StreamingMaximumFeetY = 255;
    public const float MaximumStreamingStep = 256;
    const double TouchTolerance = .00001;

    public static bool Collides(World world, Vector3 feet, float bodyHeight = BodyHeight)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireFinite(feet, nameof(feet));
        RequireBodyHeight(bodyHeight);
        if (OutsideBounds(world, feet)) return true;
        return AnySolid(world,
            feet.X - (double)HalfWidth, feet.Y, feet.Z - (double)HalfWidth,
            feet.X + (double)HalfWidth, feet.Y + (double)bodyHeight, feet.Z + (double)HalfWidth);
    }

    public static bool OverlapsPlayer(Cell block, Vector3 feet, float bodyHeight = BodyHeight)
    {
        RequireFinite(feet, nameof(feet));
        RequireBodyHeight(bodyHeight);
        return block.X + 1d > feet.X - HalfWidth + TouchTolerance && block.X < feet.X + HalfWidth - TouchTolerance &&
               block.Z + 1d > feet.Z - HalfWidth + TouchTolerance && block.Z < feet.Z + HalfWidth - TouchTolerance &&
               block.Y + 1d > feet.Y + TouchTolerance && block.Y < feet.Y + bodyHeight - TouchTolerance;
    }

    /// <summary>Only solid blocks directly below the feet count as support; walls and ceilings do not.</summary>
    public static bool HasSupport(World world, Vector3 feet)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireFinite(feet, nameof(feet));
        return AnySolid(world,
            feet.X - (double)HalfWidth, feet.Y - .002, feet.Z - (double)HalfWidth,
            feet.X + (double)HalfWidth, feet.Y, feet.Z + (double)HalfWidth);
    }

    /// <summary>Mutates feet to its nearest collision-safe position. X/Z walls and the flight ceiling are solid.</summary>
    public static CollisionResult Move(World world, ref Vector3 feet, Vector3 delta, float bodyHeight = BodyHeight)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireFinite(feet, nameof(feet));
        RequireFinite(delta, nameof(delta));
        RequireBodyHeight(bodyHeight);
        if (world.IsStreaming) ClampStreamingPosition(ref feet);
        bool hitX = MoveAxis(world, ref feet, delta.X, 0, bodyHeight);
        bool hitZ = MoveAxis(world, ref feet, delta.Z, 2, bodyHeight);
        bool hitY = MoveAxis(world, ref feet, delta.Y, 1, bodyHeight);
        return new(hitX, hitY, hitZ, hitY && delta.Y < 0);
    }

    static bool MoveAxis(World world, ref Vector3 feet, float movement, int axis, float bodyHeight)
    {
        if (movement == 0) return false;
        double origin = Axis(feet, axis);
        double requested = origin + movement;
        double limited = world.IsStreaming ? Math.Clamp((double)movement, -MaximumStreamingStep, MaximumStreamingStep) : movement;
        double target = axis == 1 ? Math.Min(origin + limited, MaximumFeetHeight(world))
            : Math.Clamp(origin + limited, MinimumHorizontal(world), MaximumHorizontal(world));
        double lowerOffset = axis == 1 ? 0 : -HalfWidth;
        double upperOffset = axis == 1 ? bodyHeight : HalfWidth;
        double startMin = origin + lowerOffset, startMax = origin + upperOffset;
        double minX = feet.X - (double)HalfWidth, maxX = feet.X + (double)HalfWidth;
        double minY = feet.Y, maxY = feet.Y + (double)bodyHeight;
        double minZ = feet.Z - (double)HalfWidth, maxZ = feet.Z + (double)HalfWidth;
        double sweepMin = Math.Min(origin, target) + lowerOffset;
        double sweepMax = Math.Max(origin, target) + upperOffset;
        if (axis == 0) { minX = sweepMin; maxX = sweepMax; }
        if (axis == 1) { minY = sweepMin; maxY = sweepMax; }
        if (axis == 2) { minZ = sweepMin; maxZ = sweepMax; }
        if (CellRange(world, 0, minX, maxX, out int fromX, out int toX) &&
            CellRange(world, 1, minY, maxY, out int fromY, out int toY) &&
            CellRange(world, 2, minZ, maxZ, out int fromZ, out int toZ))
        {
            for (int y = fromY; y <= toY; y++) for (int z = fromZ; z <= toZ; z++) for (int x = fromX; x <= toX; x++)
            {
                if (!Obstructs(world, new(x, y, z))) continue;
                int nearFace = axis == 0 ? x : axis == 1 ? y : z;
                if (movement > 0 && nearFace >= startMax - TouchTolerance)
                    target = Math.Min(target, nearFace - upperOffset);
                else if (movement < 0 && nearFace + 1 <= startMin + TouchTolerance)
                    target = Math.Max(target, nearFace + 1 - lowerOffset);
            }
        }
        float result = (float)Math.Clamp(target, -float.MaxValue, float.MaxValue);
        if (axis == 0) feet.X = result;
        if (axis == 1) feet.Y = result;
        if (axis == 2) feet.Z = result;
        return Math.Abs(target - requested) > TouchTolerance;
    }

    static double Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    static bool AnySolid(World world, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        if (!CellRange(world, 0, minX, maxX, out int fromX, out int toX) ||
            !CellRange(world, 1, minY, maxY, out int fromY, out int toY) ||
            !CellRange(world, 2, minZ, maxZ, out int fromZ, out int toZ)) return false;
        for (int y = fromY; y <= toY; y++) for (int z = fromZ; z <= toZ; z++) for (int x = fromX; x <= toX; x++)
            if (Obstructs(world, new(x, y, z))) return true;
        return false;
    }

    internal static float MinimumHorizontal(World world) => world.IsStreaming ? -World.MaximumPlayerCoordinate + BoundaryMargin : BoundaryMargin;
    internal static float MaximumHorizontal(World world) => world.IsStreaming ? World.MaximumPlayerCoordinate - BoundaryMargin : World.Size - BoundaryMargin;
    internal static float MaximumFeetHeight(World world) => world.IsStreaming ? StreamingMaximumFeetY : MaximumFeetY;

    internal static bool Obstructs(World world, Cell cell) => world.IsStreaming
        ? cell.Y < 0 || !world.IsCellAvailable(cell) || world.Solid(cell)
        : world.Solid(cell);

    static bool OutsideBounds(World world, Vector3 feet) => feet.X < MinimumHorizontal(world) || feet.X > MaximumHorizontal(world) ||
        feet.Z < MinimumHorizontal(world) || feet.Z > MaximumHorizontal(world) || feet.Y > MaximumFeetHeight(world) || world.IsStreaming && feet.Y < 0;

    static void ClampStreamingPosition(ref Vector3 feet)
    {
        feet.X = Math.Clamp(feet.X, -World.MaximumPlayerCoordinate + BoundaryMargin, World.MaximumPlayerCoordinate - BoundaryMargin);
        feet.Z = Math.Clamp(feet.Z, -World.MaximumPlayerCoordinate + BoundaryMargin, World.MaximumPlayerCoordinate - BoundaryMargin);
        feet.Y = Math.Clamp(feet.Y, -1, StreamingMaximumFeetY);
    }

    static bool CellRange(World world, int axis, double min, double max, out int from, out int to)
    {
        double lower = world.IsStreaming ? axis == 1 ? -1 : -World.MaximumPlayerCoordinate - 1 : 0;
        double upper = world.IsStreaming ? axis == 1 ? StreamingMaximumFeetY + 3 : World.MaximumPlayerCoordinate + 1 : axis == 1 ? World.Height : World.Size;
        from = 0; to = -1;
        if (max <= lower || min >= upper) return false;
        // Bound before converting to integers, including enormous finite tool inputs.
        from = (int)Math.Max(lower, Math.Floor(min + TouchTolerance));
        to = (int)Math.Min(upper - 1, Math.Floor(max - TouchTolerance));
        return from <= to;
    }

    internal static void RequireFinite(Vector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameter, "A finite vector is required.");
    }

    static void RequireBodyHeight(float height)
    {
        if (!float.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "A positive finite body height is required.");
    }
}
