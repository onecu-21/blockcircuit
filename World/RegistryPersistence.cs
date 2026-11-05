using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

public sealed partial class World
{
    internal sealed record BlockPayload(ContentId Id, JsonElement? Data);
    internal readonly Dictionary<Cell, BlockPayload> blockPayloads = [];
    internal string? StoredPath { get; set; }
    internal string? StoredGeneration { get; set; }
    public ChunkSaveReport LastSaveReport { get; internal set; } = new(0, 0, 0, false);
    public bool LastLoadRecovered { get; internal set; }
    internal readonly Dictionary<string, int> unknownInventory = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> UnknownInventory => unknownInventory;
    public ContentId GetPersistentId(Cell cell) => Get(cell) == Block.Air ? Registries.Blocks.GetId(Block.Air) : blockPayloads.TryGetValue(cell, out var value) ? value.Id : Registries.Blocks.GetId(Get(cell));
    public JsonElement? GetBlockData(Cell cell) => Get(cell) != Block.Air && blockPayloads.TryGetValue(cell, out var value) ? value.Data : null;
    bool IsMissing(Cell cell) => blockPayloads.TryGetValue(cell, out var value) && !Registries.Blocks.TryGetBlock(value.Id, out _);
    public void SetBlockData(Cell cell, JsonElement? data)
    {
        if (Get(cell) == Block.Air) throw new ArgumentException("Block data requires an occupied world cell.", nameof(cell));
        if (data?.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Undefined JSON cannot be saved.", nameof(data));
        if (currentEdit is null && !replayingHistory) history.Clear();
        blockPayloads[cell] = new(GetPersistentId(cell), data?.Clone());
        Chunks.MarkContentDirty(cell);
    }
    public sealed record CellData(Cell Cell, JsonElement Data);
    public sealed record ExtraCell(Cell Cell, string Id, JsonElement? Data, bool SwitchOn);
    public sealed record SnapshotData(int Version, string[]? Palette, int[]? Cells, Cell[]? Switches,
        Dictionary<string, int>? Counts, Vector3 Position, float Yaw, float Pitch)
    {
        public int Seed { get; init; } = 731;
        public int TerrainVersion { get; init; }
        public long TickCount { get; init; }
        public bool IsStreaming { get; init; }
        public int GenerationVersion { get; init; } = 2;
        public ColumnCoord[]? KnownColumns { get; init; } = [];
        public FallingBlockSaveState? FallingBlocks { get; init; }
        public CellData[]? BlockData { get; init; } = [];
        public ExtraCell[]? ExtraCells { get; init; } = [];
    }

    public void Save(string path, Vector3 position, float yaw, float pitch)
    {
        if (ActiveStreamer is not null)
        {
            var owner = ActiveStreamer; owner.FlushSaves(position, yaw, pitch);
            if (Path.GetFullPath(path) == owner.StoragePath) return;
        }
        ChunkSaveStore.Save(this, path, position, yaw, pitch);
    }

    public void ExportSnapshot(string path, Vector3 position, float yaw, float pitch)
    {
        if (ActiveStreamer?.SaveInProgress == true) ActiveStreamer.FlushSaves(position, yaw, pitch);
        ValidatePlayer(position, yaw, pitch, IsStreaming);
        if (IsStreaming && StoredChunkReferences.Keys.Any(c => !Chunks.Chunks.ContainsKey(c) && !Chunks.DirtyCoordinates.Contains(c)))
        { CreateCompleteExportWorld().ExportSnapshot(path, position, yaw, pitch); return; }
        var palette = new List<string>();
        var lookup = new Dictionary<ContentId, int>();
        var cells = new int[Size * Height * Size];
        for (int y = 0; y < Height; y++) for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
        {
            var cell = new Cell(x, y, z);
            var id = GetPersistentId(cell);
            if (!lookup.TryGetValue(id, out int index))
            { index = palette.Count; lookup.Add(id, index); palette.Add(id.ToString()); }
            cells[Index(cell)] = index;
        }
        var counts = new Dictionary<string, int>(unknownInventory, StringComparer.Ordinal);
        for (int i = 0; i < Inventory.Length; i++) counts[Registries.Blocks.GetId((Block)i).ToString()] = Inventory[i];
        if (counts.Values.Any(n => n < 0)) throw new InvalidDataException("Negative inventory.");
        var data = new SnapshotData(3, palette.ToArray(), cells, Switches.Where(Inside).ToArray(), counts, position, yaw, pitch)
        {
            Seed = Seed, TerrainVersion = TerrainVersion, TickCount = TickCount,
            IsStreaming = IsStreaming, GenerationVersion = GenerationVersion, KnownColumns = KnownColumns.ToArray(), FallingBlocks = FallingBlocks.Capture(),
            BlockData = blockPayloads.Where(p => Inside(p.Key) && Get(p.Key) != Block.Air && p.Value.Data.HasValue).Select(p => new CellData(p.Key, p.Value.Data!.Value)).ToArray(),
            ExtraCells = OccupiedCells().Where(cell => !Inside(cell)).Select(cell => new ExtraCell(cell, GetPersistentId(cell).ToString(), GetBlockData(cell), Switches.Contains(cell))).ToArray()
        };
        AtomicSave.BackupLegacy(path, maximumVersion: 2);
        string json = JsonSerializer.Serialize(data, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64L * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds 64 MiB.");
        AtomicSave.Write(path, json);
    }

    public (Vector3 Position, float Yaw, float Pitch) Load(string path)
    {
        ActiveStreamer?.Dispose();
        path = Path.GetFullPath(path);
        try { return LoadFile(path, path, false); }
        catch (Exception original) when (IsSaveError(original))
        {
            if (File.Exists(path + ".bak"))
                try { return LoadFile(path + ".bak", path, true); }
                catch (Exception recovery) when (IsSaveError(recovery))
                { throw new InvalidDataException("Both current and recovery saves are invalid.", new AggregateException(original, recovery)); }
            throw new InvalidDataException("Invalid world save.", original);
        }
    }
    static bool IsSaveError(Exception error) => error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException or KeyNotFoundException;
    (Vector3 Position, float Yaw, float Pitch) LoadFile(string file, string storePath, bool recovered)
    {
        if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidDataException("World metadata exceeds 64 MiB.");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int value))
            throw new InvalidDataException("Missing save version.");
        if (value == 4) return ChunkSaveStore.Load(this, storePath, document.RootElement, recovered);
        (Vector3 Position, float Yaw, float Pitch) result;
        if (value is 1 or 2) result = LoadLegacy(file);
        else if (value == 3)
        {
            var data = JsonSerializer.Deserialize<SnapshotData>(document.RootElement.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("Empty save.");
            result = LoadSnapshot(data);
        }
        else throw new InvalidDataException("Unsupported save version.");
        StoredPath = recovered ? storePath : null; StoredGeneration = null; LastLoadRecovered = recovered;
        return result;
    }

    (Vector3 Position, float Yaw, float Pitch) LoadSnapshot(SnapshotData data)
    {
        ValidatePlayer(data.Position, data.Yaw, data.Pitch, data.IsStreaming);
        FallingBlockSystem.ValidateState(data.FallingBlocks);
        if (data.KnownColumns is null || data.GenerationVersion != 2 || data.KnownColumns.Distinct().Count() != data.KnownColumns.Length || data.KnownColumns.Any(c => !new ChunkCoord(c.X, 0, c.Z).IsValid)) throw new InvalidDataException("Invalid streaming metadata.");
        if (data.TerrainVersion is not (0 or 1 or 2) || data.TickCount < 0 ||
            data.Palette is null || data.Palette.Length is < 1 or > 44800 ||
            data.Cells is null || data.Cells.Length != Size * Height * Size ||
            data.Switches is null || data.Counts is null || data.BlockData is null || data.ExtraCells is null)
            throw new InvalidDataException("Invalid registry snapshot.");
        var ids = data.Palette.Select(s => new ContentId(s)).ToArray();
        if (ids.Distinct().Count() != ids.Length || data.Cells.Any(i => (uint)i >= ids.Length))
            throw new InvalidDataException("Invalid block palette.");
        var chunks = new ChunkManager(Registries.Blocks);
        var payloads = new Dictionary<Cell, BlockPayload>();
        for (int y = 0; y < Height; y++) for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
        {
            var cell = new Cell(x, y, z);
            var id = ids[data.Cells[Index(cell)]];
            if (!Registries.Blocks.TryGetBlock(id, out var block))
            { block = Block.Stone; payloads.Add(cell, new(id, null)); }
            chunks.Set(cell, block);
        }
        foreach (var entry in data.BlockData)
        {
            if (entry is null || !Inside(entry.Cell) || chunks.Get(entry.Cell) == Block.Air || entry.Data.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Invalid block payload location.");
            if (payloads.TryGetValue(entry.Cell, out var existing) && existing.Data.HasValue)
                throw new InvalidDataException("Duplicate block payload.");
            payloads[entry.Cell] = new(ids[data.Cells[Index(entry.Cell)]], entry.Data.Clone());
        }
        if (data.Switches.Distinct().Count() != data.Switches.Length ||
            data.Switches.Any(cell => !Inside(cell) || chunks.Get(cell) != Block.Lever))
            throw new InvalidDataException("Invalid switch state.");
        var allSwitches = data.Switches.ToHashSet();
        var extraLocations = new HashSet<Cell>();
        foreach (var extra in data.ExtraCells)
        {
            if (extra is null || Inside(extra.Cell) || !extraLocations.Add(extra.Cell) || extra.Data?.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Invalid extra cell.");
            var id = new ContentId(extra.Id);
            bool known = Registries.Blocks.TryGetBlock(id, out var block);
            if (!known) block = Block.Stone;
            if (block == Block.Air || extra.SwitchOn && (!known || block != Block.Lever))
                throw new InvalidDataException("Invalid extra block state.");
            chunks.Set(extra.Cell, block);
            if (!known || extra.Data.HasValue) payloads[extra.Cell] = new(id, extra.Data?.Clone());
            if (extra.SwitchOn) allSwitches.Add(extra.Cell);
        }
        int[] inventory = new int[Registries.Blocks.Count];
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, count) in data.Counts)
        {
            var id = new ContentId(name);
            if (count < 0) throw new InvalidDataException("Negative inventory.");
            if (Registries.Blocks.TryGetBlock(id, out var block)) inventory[(int)block] = count;
            else unknown.Add(name, count);
        }
        InstallState(chunks, payloads, inventory, unknown, allSwitches, data.Seed, data.TerrainVersion, data.TickCount);
        SetStreamingState(data.IsStreaming, data.GenerationVersion, data.KnownColumns);
        if (data.FallingBlocks is not null) FallingBlocks.Restore(data.FallingBlocks);
        return (data.Position, data.Yaw, Math.Clamp(data.Pitch, -1.5f, 1.5f));
    }
    internal void InstallState(ChunkManager chunks, Dictionary<Cell, BlockPayload> payloads, int[] inventory,
        Dictionary<string, int> unknown, IEnumerable<Cell> switches, int seed, int terrainVersion, long ticks)
    {
        StoredPath = null; StoredGeneration = null; LastLoadRecovered = false;
        Chunks = chunks; BindChunks(chunks); blockPayloads.Clear();
        foreach (var pair in payloads) blockPayloads.Add(pair.Key, pair.Value);
        unknownInventory.Clear(); foreach (var pair in unknown) unknownInventory.Add(pair.Key, pair.Value);
        Inventory = inventory; Switches = switches.ToHashSet();
        Seed = seed; TerrainVersion = terrainVersion; TickCount = ticks;
        SetStreamingState(false, 2); FallingBlocks.Restore(null);
        RecomputePower(); ClearHistory();
        foreach (var coord in Chunks.Chunks.Keys) FallingBlocks.OnChunkLoaded(coord);
    }
    internal IEnumerable<Cell> OccupiedCells()
    {
        foreach (var (coord, chunk) in Chunks.Chunks)
        {
            var origin = coord.Origin;
            for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                if (chunk.Get(new(x, y, z)) != Block.Air) yield return origin + new Cell(x, y, z);
        }
    }
    internal static void ValidatePlayer(Vector3 position, float yaw, float pitch, bool streaming = false)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            !float.IsFinite(yaw) || !float.IsFinite(pitch) ||
            (streaming ? Math.Abs(position.X) > MaximumPlayerCoordinate || Math.Abs(position.Z) > MaximumPlayerCoordinate || position.Y < 0 || position.Y > 255 :
                position.X < .3f || position.X > Size - .3f || position.Z < .3f || position.Z > Size - .3f || position.Y < 0 || position.Y > Height + 10))
            throw new InvalidDataException("Invalid saved player state.");
    }
}
