using System.Collections.ObjectModel;

namespace BlockCircuit;

/// <summary>Sparse resident chunks, separate mesh revisions, and versioned persistence dirtiness.</summary>
public sealed class ChunkManager
{
    readonly Dictionary<ChunkCoord, Chunk> chunks = [];
    readonly Dictionary<ChunkCoord, long> contentRevisions = [];
    readonly HashSet<ChunkCoord> dirty = [];
    long revision;
    long contentEpoch;
    internal Func<Cell, bool>? CanEdit { get; set; }
    internal Action<Cell>? OnCellChanged { get; set; }
    internal Action<ChunkCoord>? OnChunkRemoved { get; set; }
    readonly BlockRegistry blocks;
    public IReadOnlyDictionary<ChunkCoord, Chunk> Chunks { get; }
    public IReadOnlyCollection<ChunkCoord> DirtyCoordinates => dirty.ToArray();

    public ChunkManager(BlockRegistry? blocks = null)
    {
        this.blocks = blocks ?? GameRegistries.Default.Blocks;
        Chunks = new ReadOnlyDictionary<ChunkCoord, Chunk>(chunks);
    }

    public Block Get(Cell world) => chunks.TryGetValue(ChunkCoord.FromWorld(world), out var chunk)
        ? chunk.Get(ChunkCoord.ToLocal(world)) : Block.Air;

    public bool Set(Cell world, Block block)
    {
        if (CanEdit is not null && !CanEdit(world)) throw new InvalidOperationException("Wait for this chunk to load before editing it.");
        if (!blocks.IsValid(block)) throw new ArgumentOutOfRangeException(nameof(block), "Unknown block ID.");
        var coord = ChunkCoord.FromWorld(world);
        if (!chunks.TryGetValue(coord, out var chunk))
        {
            if (block == Block.Air) return false;
            chunk = new Chunk(coord);
            chunks.Add(coord, chunk);
        }
        if (!chunk.Set(ChunkCoord.ToLocal(world), block)) return false;
        TrackContent(coord);
        // Mesh neighbors may change without requiring their block payload to be saved.
        MarkDirty(world);
        OnCellChanged?.Invoke(world);
        if (chunk.NonAirCount == 0)
        {
            chunks.Remove(coord);
            chunk.Lifecycle.Retire();
            OnChunkRemoved?.Invoke(coord);
        }
        return true;
    }

    public bool Remove(ChunkCoord coord)
    {
        RequireValid(coord);
        if (!chunks.Remove(coord, out var chunk)) return false;
        TrackContent(coord);
        chunk.Lifecycle.Retire();
        OnChunkRemoved?.Invoke(coord);
        Touch(new(coord.X - 1, coord.Y, coord.Z));
        Touch(new(coord.X + 1, coord.Y, coord.Z));
        Touch(new(coord.X, coord.Y - 1, coord.Z));
        Touch(new(coord.X, coord.Y + 1, coord.Z));
        Touch(new(coord.X, coord.Y, coord.Z - 1));
        Touch(new(coord.X, coord.Y, coord.Z + 1));
        return true;
    }

    internal void Install(ChunkCoord coord, Block[] values, bool dirtyContent)
    {
        RequireValid(coord);
        if (chunks.ContainsKey(coord)) throw new InvalidOperationException("Chunk already resident.");
        if (values.Any(b => !blocks.IsValid(b))) throw new InvalidDataException("Unknown chunk runtime ID.");
        var chunk = Chunk.FromBlocks(coord, values);
        if (chunk.NonAirCount == 0) return;
        chunks.Add(coord, chunk);
        if (dirtyContent) TrackContent(coord);
        Touch(coord);
        foreach (var offset in World.Neighbors) Touch(new(coord.X + offset.X, coord.Y + offset.Y, coord.Z + offset.Z));
    }
    internal void RecordDeletion(ChunkCoord coord) { RequireValid(coord); TrackContent(coord); }
    internal bool Evict(ChunkCoord coord)
    {
        if (!chunks.TryGetValue(coord, out var chunk)) return false;
        if (chunk.IsDirty || dirty.Contains(coord)) throw new InvalidOperationException("Cannot evict unsaved content.");
        chunks.Remove(coord); contentRevisions.Remove(coord); chunk.Lifecycle.Retire(); OnChunkRemoved?.Invoke(coord);
        foreach (var offset in World.Neighbors) Touch(new(coord.X + offset.X, coord.Y + offset.Y, coord.Z + offset.Z));
        return true;
    }

    /// <summary>Content-only changes such as block data and lever state must also be persisted.</summary>
    public void MarkContentDirty(Cell world)
    {
        var coord = ChunkCoord.FromWorld(world);
        // Do not allocate empty chunk records solely for a no-op on untouched air.
        if (!chunks.ContainsKey(coord) && !contentRevisions.ContainsKey(coord)) return;
        TrackContent(coord);
    }

    public long GetContentRevision(ChunkCoord coord)
    {
        RequireValid(coord);
        return contentRevisions.GetValueOrDefault(coord);
    }

    /// <summary>A stale save acknowledgement cannot clear newer edits or a recreated chunk.</summary>
    public bool MarkSaved(ChunkCoord coord, long contentRevision)
    {
        RequireValid(coord);
        if (!dirty.Contains(coord) || contentRevisions.GetValueOrDefault(coord) != contentRevision) return false;
        dirty.Remove(coord);
        if (chunks.TryGetValue(coord, out var chunk)) chunk.IsDirty = false;
        else contentRevisions.Remove(coord);
        // The global epoch never repeats, so forgotten committed tombstones cannot
        // make an old save acknowledgement match a future recreated chunk.
        return true;
    }

    /// <summary>Use after loading a fully validated snapshot, before publishing the manager.</summary>
    public void MarkAllSaved()
    {
        dirty.Clear();
        foreach (var chunk in chunks.Values) chunk.IsDirty = false;
        foreach (var coord in contentRevisions.Keys.Where(coord => !chunks.ContainsKey(coord)).ToArray())
            contentRevisions.Remove(coord);
    }

    /// <summary>Mesh invalidation only; neighboring content remains clean on disk.</summary>
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

    void TrackContent(ChunkCoord coord)
    {
        long next = checked(contentEpoch + 1);
        contentEpoch = next;
        contentRevisions[coord] = next;
        dirty.Add(coord);
        if (chunks.TryGetValue(coord, out var chunk))
        {
            chunk.ContentRevision = next;
            chunk.IsDirty = true;
        }
    }

    void Touch(ChunkCoord coord)
    {
        if (!coord.IsValid || !chunks.TryGetValue(coord, out var chunk)) return;
        chunk.Lifecycle.Invalidate();
        chunk.Revision = checked(++revision);
    }

    static void RequireValid(ChunkCoord coord)
    {
        if (!coord.IsValid) throw new ArgumentOutOfRangeException(nameof(coord), "Chunk origin must fit 32-bit world coordinates.");
    }
}
