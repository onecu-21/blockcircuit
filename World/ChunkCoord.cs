namespace BlockCircuit;

/// <summary>A 16³ chunk address. Floor division keeps negative world coordinates contiguous.</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z)
{
    public const int Size = 16;
    public Cell Origin => new(X * Size, Y * Size, Z * Size);

    public static ChunkCoord FromWorld(Cell cell) => new(FloorDivide(cell.X), FloorDivide(cell.Y), FloorDivide(cell.Z));
    public static Cell ToLocal(Cell cell) => new(FloorMod(cell.X), FloorMod(cell.Y), FloorMod(cell.Z));

    private static int FloorDivide(int value)
    {
        int quotient = value / Size;
        return value % Size < 0 ? quotient - 1 : quotient;
    }

    private static int FloorMod(int value)
    {
        int remainder = value % Size;
        return remainder < 0 ? remainder + Size : remainder;
    }
}
