namespace BlockCircuit;

/// <summary>Block storage is allocated only while a chunk contains non-air blocks.</summary>
public sealed class Chunk
{
    private byte[]? blocks;
    public ChunkCoord Coord { get; }
    public long Revision { get; internal set; }
    public int NonAirCount { get; private set; }

    public Chunk(ChunkCoord coord) => Coord = coord;

    public Block Get(Cell local)
    {
        int index = Index(local);
        return blocks is null ? Block.Air : (Block)blocks[index];
    }

    internal bool Set(Cell local, Block block)
    {
        int index = Index(local);
        var previous = blocks is null ? Block.Air : (Block)blocks[index];
        if (previous == block) return false;
        blocks ??= new byte[ChunkCoord.Size * ChunkCoord.Size * ChunkCoord.Size];
        blocks[index] = (byte)block;
        if (previous == Block.Air) NonAirCount++;
        if (block == Block.Air) NonAirCount--;
        if (NonAirCount == 0) blocks = null;
        return true;
    }

    private static int Index(Cell local)
    {
        if ((uint)local.X >= ChunkCoord.Size || (uint)local.Y >= ChunkCoord.Size || (uint)local.Z >= ChunkCoord.Size)
            throw new ArgumentOutOfRangeException(nameof(local), "Chunk-local coordinates must be between 0 and 15.");
        return (local.Y * ChunkCoord.Size + local.Z) * ChunkCoord.Size + local.X;
    }
}
