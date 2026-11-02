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
    const double TouchTolerance = .00001;

    public static bool Collides(World world, Vector3 feet)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireFinite(feet, nameof(feet));
        if (feet.X < BoundaryMargin || feet.Z < BoundaryMargin ||
            feet.X > World.Size - BoundaryMargin || feet.Z > World.Size - BoundaryMargin || feet.Y > MaximumFeetY)
            return true;
        return AnySolid(world,
            feet.X - (double)HalfWidth, feet.Y, feet.Z - (double)HalfWidth,
            feet.X + (double)HalfWidth, feet.Y + (double)BodyHeight, feet.Z + (double)HalfWidth);
    }

    public static bool OverlapsPlayer(Cell block, Vector3 feet)
    {
        RequireFinite(feet, nameof(feet));
        return block.X + 1d > feet.X - HalfWidth + TouchTolerance && block.X < feet.X + HalfWidth - TouchTolerance &&
               block.Z + 1d > feet.Z - HalfWidth + TouchTolerance && block.Z < feet.Z + HalfWidth - TouchTolerance &&
               block.Y + 1d > feet.Y + TouchTolerance && block.Y < feet.Y + BodyHeight - TouchTolerance;
    }

    /// <summary>Mutates feet to its nearest collision-safe position. X/Z walls and the flight ceiling are solid.</summary>
    public static CollisionResult Move(World world, ref Vector3 feet, Vector3 delta)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireFinite(feet, nameof(feet));
        RequireFinite(delta, nameof(delta));
        bool hitX = MoveAxis(world, ref feet, delta.X, 0);
        bool hitZ = MoveAxis(world, ref feet, delta.Z, 2);
        bool hitY = MoveAxis(world, ref feet, delta.Y, 1);
        return new(hitX, hitY, hitZ, hitY && delta.Y < 0);
    }

    static bool MoveAxis(World world, ref Vector3 feet, float movement, int axis)
    {
        if (movement == 0) return false;
        double origin = Axis(feet, axis);
        double requested = origin + movement;
        double target = axis == 1 ? Math.Min(requested, MaximumFeetY)
            : Math.Clamp(requested, BoundaryMargin, World.Size - BoundaryMargin);
        double lowerOffset = axis == 1 ? 0 : -HalfWidth;
        double upperOffset = axis == 1 ? BodyHeight : HalfWidth;
        double startMin = origin + lowerOffset, startMax = origin + upperOffset;
        double minX = feet.X - (double)HalfWidth, maxX = feet.X + (double)HalfWidth;
        double minY = feet.Y, maxY = feet.Y + (double)BodyHeight;
        double minZ = feet.Z - (double)HalfWidth, maxZ = feet.Z + (double)HalfWidth;
        double sweepMin = Math.Min(origin, target) + lowerOffset;
        double sweepMax = Math.Max(origin, target) + upperOffset;
        if (axis == 0) { minX = sweepMin; maxX = sweepMax; }
        if (axis == 1) { minY = sweepMin; maxY = sweepMax; }
        if (axis == 2) { minZ = sweepMin; maxZ = sweepMax; }
        if (CellRange(minX, maxX, World.Size, out int fromX, out int toX) &&
            CellRange(minY, maxY, World.Height, out int fromY, out int toY) &&
            CellRange(minZ, maxZ, World.Size, out int fromZ, out int toZ))
        {
            for (int y = fromY; y <= toY; y++) for (int z = fromZ; z <= toZ; z++) for (int x = fromX; x <= toX; x++)
            {
                if (!world.Solid(new(x, y, z))) continue;
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
        if (!CellRange(minX, maxX, World.Size, out int fromX, out int toX) ||
            !CellRange(minY, maxY, World.Height, out int fromY, out int toY) ||
            !CellRange(minZ, maxZ, World.Size, out int fromZ, out int toZ)) return false;
        for (int y = fromY; y <= toY; y++) for (int z = fromZ; z <= toZ; z++) for (int x = fromX; x <= toX; x++)
            if (world.Solid(new(x, y, z))) return true;
        return false;
    }

    static bool CellRange(double min, double max, int limit, out int from, out int to)
    {
        from = 0; to = -1;
        if (max <= 0 || min >= limit) return false;
        // Clamp doubles before integer conversion, including enormous finite test inputs.
        from = (int)Math.Max(0, Math.Floor(min + TouchTolerance));
        to = (int)Math.Min(limit - 1, Math.Floor(max - TouchTolerance));
        return from <= to;
    }

    internal static void RequireFinite(Vector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameter, "A finite vector is required.");
    }
}
