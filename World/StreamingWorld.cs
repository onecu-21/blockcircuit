using System.Numerics;

namespace BlockCircuit;

public readonly record struct ColumnCoord(int X, int Z)
{
    public static ColumnCoord FromPosition(Vector3 position) => new(
        ChunkCoord.FromWorld(new((int)MathF.Floor(position.X), 0, (int)MathF.Floor(position.Z))).X,
        ChunkCoord.FromWorld(new(0, 0, (int)MathF.Floor(position.Z))).Z);
    public static ColumnCoord FromChunk(ChunkCoord chunk) => new(chunk.X, chunk.Z);
}

public sealed partial class World
{
    public const float MaximumPlayerCoordinate = 1_000_000f;
    public bool IsStreaming { get; private set; }
    internal WorldStreamer? ActiveStreamer { get; set; }
    public int GenerationVersion { get; internal set; } = StreamingTerrainGenerator.Version;
    public FallingBlockSystem FallingBlocks { get; }
    internal readonly HashSet<ColumnCoord> KnownColumns = [];
    internal readonly HashSet<ColumnCoord> AvailableColumns = [];
    internal readonly Dictionary<ChunkCoord, ChunkFileReference> StoredChunkReferences = [];
    internal readonly HashSet<string> RetainedChunkFiles = new(StringComparer.Ordinal);

    public static World CreateStreaming(int seed = 2026, GameRegistries? registries = null)
    {
        var world = new World(false, seed, registries: registries);
        world.TerrainVersion = StreamingTerrainGenerator.Version;
        world.EnableStreaming();
        return world;
    }
    internal void BuildStarterCircuit()
    {
        if (Enumerable.Range(18, 7).Any(x => Get(new(x, 10, 18)) != Block.Air || !Solid(new(x, 9, 18)))) return;
        CreateDemoCircuit(); RecomputePower();
    }
    public void EnableStreaming()
    {
        if (!IsStreaming && TerrainVersion < 2)
            for (int z = 0; z <= (Size - 1) / 16; z++) for (int x = 0; x <= (Size - 1) / 16; x++) KnownColumns.Add(new(x, z));
        IsStreaming = true;
        KnownColumns.UnionWith(Chunks.Chunks.Keys.Select(ColumnCoord.FromChunk));
    }
    public bool IsCellAvailable(Cell cell)
    {
        if (!IsStreaming) return true;
        if (cell.Y < 0 || cell.Y > 257 || Math.Abs((long)cell.X) > MaximumPlayerCoordinate || Math.Abs((long)cell.Z) > MaximumPlayerCoordinate) return false;
        return Chunks.Chunks.ContainsKey(ChunkCoord.FromWorld(cell)) ||
            AvailableColumns.Contains(ColumnCoord.FromChunk(ChunkCoord.FromWorld(cell)));
    }
    internal bool CanEditStreamingCell(Cell cell)
    {
        if (!IsStreaming) return true;
        if (StoredChunkReferences.ContainsKey(ChunkCoord.FromWorld(cell)) && !Chunks.Chunks.ContainsKey(ChunkCoord.FromWorld(cell)) &&
            !(cell.Y is >= 0 and <= 255 && AvailableColumns.Contains(ColumnCoord.FromChunk(ChunkCoord.FromWorld(cell))))) return false;
        return ActiveStreamer is null || IsCellAvailable(cell) && cell.Y is >= 0 and <= 255;
    }
    internal void SetStreamingState(bool streaming, int generatorVersion, IEnumerable<ColumnCoord>? known = null)
    {
        IsStreaming = streaming; GenerationVersion = generatorVersion;
        KnownColumns.Clear(); AvailableColumns.Clear(); StoredChunkReferences.Clear(); RetainedChunkFiles.Clear();
        if (known is not null) KnownColumns.UnionWith(known);
    }
    internal void ImportChunk(StreamingChunkData data, bool dirty)
    {
        if (Chunks.Chunks.ContainsKey(data.Coord)) return;
        Chunks.Install(data.Coord, data.Blocks, dirty);
        foreach (var pair in data.Payloads) blockPayloads[pair.Key] = pair.Value;
        foreach (var cell in data.Switches) Switches.Add(cell);
        var origin = data.Coord.Origin;
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            var block = data.Blocks[(y * 16 + z) * 16 + x];
            if (block is Block.Wire or Block.Lever or Block.Lamp)
                Circuits.EnqueueBlockChange(origin + new Cell(x, y, z), Block.Air, block);
        }
        FallingBlocks.OnChunkLoaded(data.Coord);
    }
    internal void EvictColumn(ColumnCoord column)
    {
        var coords = Chunks.Chunks.Keys.Where(c => ColumnCoord.FromChunk(c) == column).ToArray();
        foreach (var coord in coords)
        {
            var chunk = Chunks.Chunks[coord];
            if (chunk.IsDirty) throw new InvalidOperationException("Save modified chunks before unloading.");
            var origin = coord.Origin;
            for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
            {
                var block = chunk.Get(new(x, y, z));
                if (block is Block.Wire or Block.Lever or Block.Lamp)
                    Circuits.EnqueueBlockChange(origin + new Cell(x, y, z), block, Block.Air);
            }
            Chunks.Evict(coord);
        }
        AvailableColumns.Remove(column);
        Circuits.ProcessPending();
    }
    internal World CreateCompleteExportWorld()
    {
        var result = new World(false, registries: Registries);
        result.CopyMetadataFrom(this);
        var dirty = Chunks.DirtyCoordinates.ToHashSet();
        foreach (var entry in StoredChunkReferences.Values)
            if (!Chunks.Chunks.ContainsKey(entry.Coord) && !dirty.Contains(entry.Coord))
                result.ImportChunk(ChunkSaveStore.ReadStreamingChunk(StoredPath!, entry, Registries, CancellationToken.None), true);
        foreach (var coord in Chunks.Chunks.Keys)
            result.ImportChunk(StreamingChunkData.Decode(Registries, ChunkSaveStore.CaptureChunk(this, coord)), true);
        result.FallingBlocks.Restore(FallingBlocks.Capture()); result.StoredChunkReferences.Clear();
        result.StoredPath = null; result.StoredGeneration = null;
        return result;
    }
    internal void CopyMetadataFrom(World source)
    {
        Seed = source.Seed; TerrainVersion = source.TerrainVersion; TickCount = source.TickCount;
        IsStreaming = source.IsStreaming; GenerationVersion = source.GenerationVersion;
        Inventory = source.Inventory.ToArray();
        unknownInventory.Clear(); foreach (var pair in source.unknownInventory) unknownInventory.Add(pair.Key, pair.Value);
        KnownColumns.UnionWith(source.KnownColumns);
        StoredPath = source.StoredPath; StoredGeneration = source.StoredGeneration; LastLoadRecovered = source.LastLoadRecovered;
        foreach (var pair in source.StoredChunkReferences) StoredChunkReferences.Add(pair.Key, pair.Value);
        RetainedChunkFiles.UnionWith(source.RetainedChunkFiles);
        FallingBlocks.Restore(source.FallingBlocks.Capture());
    }
}

internal sealed record StreamingChunkData(ChunkCoord Coord, Block[] Blocks,
    Dictionary<Cell, World.BlockPayload> Payloads, Cell[] Switches)
{
    internal static StreamingChunkData Decode(GameRegistries registries, ChunkFileData data, ChunkFileReference? reference = null)
    {
        if (data is null || data.Version != 1 || !data.Coord.IsValid || reference is not null && data.Coord != reference.Coord ||
            data.Palette is null || data.Palette.Length is < 1 or > 4096 || data.Cells is null || data.Cells.Length != 4096 ||
            data.Switches is null || data.BlockData is null) throw new InvalidDataException("Invalid streamed chunk.");
        var ids = data.Palette.Select(s => new ContentId(s)).ToArray();
        if (ids.Distinct().Count() != ids.Length || data.Cells.Any(i => (uint)i >= ids.Length)) throw new InvalidDataException("Invalid streamed palette.");
        var blocks = new Block[4096];
        var payloads = new Dictionary<Cell, World.BlockPayload>();
        var origin = data.Coord.Origin;
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            int index = (y * 16 + z) * 16 + x;
            var id = ids[data.Cells[index]];
            if (!registries.Blocks.TryGetBlock(id, out var block))
            { block = Block.Stone; payloads.Add(origin + new Cell(x, y, z), new(id, null)); }
            blocks[index] = block;
        }
        foreach (var item in data.BlockData)
        {
            if (item is null || ChunkCoord.FromWorld(item.Cell) != data.Coord || item.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                throw new InvalidDataException("Invalid streamed block data.");
            var local = ChunkCoord.ToLocal(item.Cell);
            int index = (local.Y * 16 + local.Z) * 16 + local.X;
            if (blocks[index] == Block.Air || payloads.TryGetValue(item.Cell, out var prior) && prior.Data.HasValue)
                throw new InvalidDataException("Invalid streamed block data location.");
            payloads[item.Cell] = new(ids[data.Cells[index]], item.Data.Clone());
        }
        var switches = new HashSet<Cell>();
        foreach (var cell in data.Switches)
        {
            var local = ChunkCoord.ToLocal(cell);
            if (ChunkCoord.FromWorld(cell) != data.Coord || blocks[(local.Y * 16 + local.Z) * 16 + local.X] != Block.Lever || !switches.Add(cell))
                throw new InvalidDataException("Invalid streamed switch.");
        }
        string signature = string.Join(";", switches.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).Select(c => $"{c.X},{c.Y},{c.Z}"));
        if (reference is not null && signature != reference.SwitchSignature) throw new InvalidDataException("Streamed switch checksum mismatch.");
        return new(data.Coord, blocks, payloads, switches.ToArray());
    }
}
