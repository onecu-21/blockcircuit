using System.Numerics;

namespace BlockCircuit;

public static class VoxelRaycaster
{
    /// <summary>
    /// Traverses voxel boundaries using a normalized ray and includes the range endpoint.
    /// The starting cell is floor(origin); a starting hit returns that cell as Previous too.
    /// An exact boundary moving negatively crosses at distance zero. Simultaneous crossings
    /// visit X, then Y, then Z, testing each intermediate voxel so Previous stays face-adjacent.
    /// Zero/non-finite directions, non-finite origins and nonpositive/non-finite ranges miss.
    /// </summary>
    public static (Cell Hit, Cell Previous)? Cast(Func<Cell, Block> getter, Vector3 origin, Vector3 direction, float range = 6)
    {
        ArgumentNullException.ThrowIfNull(getter);
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(range) || range <= 0) return null;
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
        if (length == 0) return null;
        double ox = Math.Floor(origin.X), oy = Math.Floor(origin.Y), oz = Math.Floor(origin.Z);
        if (ox < int.MinValue || ox > int.MaxValue || oy < int.MinValue || oy > int.MaxValue || oz < int.MinValue || oz > int.MaxValue) return null;
        var cell = new Cell((int)ox, (int)oy, (int)oz);
        var previous = cell;
        double dx = direction.X / length, dy = direction.Y / length, dz = direction.Z / length;
        int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
        double deltaX = sx == 0 ? double.PositiveInfinity : Math.Abs(1 / dx);
        double deltaY = sy == 0 ? double.PositiveInfinity : Math.Abs(1 / dy);
        double deltaZ = sz == 0 ? double.PositiveInfinity : Math.Abs(1 / dz);
        double nextX = FirstBoundary(origin.X, cell.X, dx, sx);
        double nextY = FirstBoundary(origin.Y, cell.Y, dy, sy);
        double nextZ = FirstBoundary(origin.Z, cell.Z, dz, sz);

        while (true)
        {
            if (getter(cell) != Block.Air) return (cell, previous);
            previous = cell;
            if (nextX <= nextY && nextX <= nextZ)
            {
                if (nextX > range || WouldOverflow(cell.X, sx)) return null;
                cell = cell with { X = cell.X + sx };
                nextX += deltaX;
            }
            else if (nextY <= nextZ)
            {
                if (nextY > range || WouldOverflow(cell.Y, sy)) return null;
                cell = cell with { Y = cell.Y + sy };
                nextY += deltaY;
            }
            else
            {
                if (nextZ > range || WouldOverflow(cell.Z, sz)) return null;
                cell = cell with { Z = cell.Z + sz };
                nextZ += deltaZ;
            }
        }
    }

    private static double FirstBoundary(float origin, int cell, double direction, int step) => step == 0
        ? double.PositiveInfinity : ((step > 0 ? (double)cell + 1 : cell) - origin) / direction;
    private static bool WouldOverflow(int cell, int step) => step > 0 ? cell == int.MaxValue : cell == int.MinValue;
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
