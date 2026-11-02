using System.Collections.ObjectModel;

namespace BlockCircuit;

/// <summary>Sparse chunk ownership and mesh invalidation, independent of the world's current finite bounds.</summary>
public sealed class ChunkManager
{
    private readonly Dictionary<ChunkCoord, Chunk> chunks = [];
    private long revision;
    public IReadOnlyDictionary<ChunkCoord, Chunk> Chunks { get; }

    public ChunkManager() => Chunks = new ReadOnlyDictionary<ChunkCoord, Chunk>(chunks);

    public Block Get(Cell world) => chunks.TryGetValue(ChunkCoord.FromWorld(world), out var chunk)
        ? chunk.Get(ChunkCoord.ToLocal(world)) : Block.Air;

    public bool Set(Cell world, Block block)
    {
        if (!BlockCatalog.IsValid(block)) throw new ArgumentOutOfRangeException(nameof(block), "Unknown block ID.");
        var coord = ChunkCoord.FromWorld(world);
        if (!chunks.TryGetValue(coord, out var chunk))
        {
            if (block == Block.Air) return false;
            chunk = new Chunk(coord);
            chunks.Add(coord, chunk);
        }
        if (!chunk.Set(ChunkCoord.ToLocal(world), block)) return false;
        // Invalidate adjacent face meshes before releasing the last block's chunk.
        MarkDirty(world);
        if (chunk.NonAirCount == 0) chunks.Remove(coord);
        return true;
    }

    public bool Remove(ChunkCoord coord)
    {
        if (!chunks.Remove(coord)) return false;
        Touch(new(coord.X - 1, coord.Y, coord.Z));
        Touch(new(coord.X + 1, coord.Y, coord.Z));
        Touch(new(coord.X, coord.Y - 1, coord.Z));
        Touch(new(coord.X, coord.Y + 1, coord.Z));
        Touch(new(coord.X, coord.Y, coord.Z - 1));
        Touch(new(coord.X, coord.Y, coord.Z + 1));
        return true;
    }

    public void MarkDirty(Cell world)
    {
        var coord = ChunkCoord.FromWorld(world);
        var local = ChunkCoord.ToLocal(world);
        Touch(coord);
        if (local.X == 0) Touch(new(coord.X - 1, coord.Y, coord.Z));
        if (local.X == ChunkCoord.Size - 1) Touch(new(coord.X + 1, coord.Y, coord.Z));
        if (local.Y == 0) Touch(new(coord.X, coord.Y - 1, coord.Z));
        if (local.Y == ChunkCoord.Size - 1) Touch(new(coord.X, coord.Y + 1, coord.Z));
        if (local.Z == 0) Touch(new(coord.X, coord.Y, coord.Z - 1));
        if (local.Z == ChunkCoord.Size - 1) Touch(new(coord.X, coord.Y, coord.Z + 1));
    }

    private void Touch(ChunkCoord coord)
    {
        if (chunks.TryGetValue(coord, out var chunk)) chunk.Revision = ++revision;
    }
}
