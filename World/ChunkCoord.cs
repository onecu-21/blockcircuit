namespace BlockCircuit;

/// <summary>A 16³ chunk address. Floor division keeps negative world coordinates contiguous.</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z)
{
    public const int Size = 16;
    public const int Minimum = int.MinValue / Size;
    public const int Maximum = int.MaxValue / Size;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => X is >= Minimum and <= Maximum && Y is >= Minimum and <= Maximum && Z is >= Minimum and <= Maximum;
    [System.Text.Json.Serialization.JsonIgnore]
    public Cell Origin => new(checked(X * Size), checked(Y * Size), checked(Z * Size));

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
