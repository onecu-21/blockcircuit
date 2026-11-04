using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlockCircuit;

public enum SaveCheckpoint { ChunksWritten, BeforeManifestCommit }
public sealed record ChunkSaveReport(int WrittenChunks, int ReusedChunks, int RemovedChunks, bool MigratedLegacy);
public sealed record ChunkFileReference(ChunkCoord Coord, string File, string Sha256, string SwitchSignature);
public sealed record ChunkWorldManifest(int Version, string Generation, int Seed, int TerrainVersion, long TickCount,
    Vector3 Position, float Yaw, float Pitch, Dictionary<string, int> Counts, ChunkFileReference[] Chunks);
public sealed record ChunkFileData(int Version, ChunkCoord Coord, string[] Palette, int[] Cells, Cell[] Switches, World.CellData[] BlockData);

/// <summary>Immutable chunk files committed by atomically replacing a small world manifest.</summary>
public static class ChunkSaveStore
{
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    static readonly Regex ChunkName = new(@"^-?\d+_-?\d+_-?\d+-[a-f0-9]{32}\.json$", RegexOptions.CultureInvariant);
    const long MaximumChunkBytes = 32 * 1024 * 1024;

    public static void Save(World world, string path, Vector3 position, float yaw, float pitch,
        Action<SaveCheckpoint>? checkpoint = null)
    {
        World.ValidatePlayer(position, yaw, pitch);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writerLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string directory = path + ".chunks";
        Directory.CreateDirectory(directory);
        ValidateDataDirectory(directory);
        bool migrated = false, primaryHealthy = !(world.LastLoadRecovered && world.StoredPath == path);
        ChunkWorldManifest? previous = null;
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                int version = document.RootElement.GetProperty("Version").GetInt32();
                if (version == 4) previous = ParseManifest(document.RootElement);
                else if (version is 1 or 2 or 3) migrated = true;
                else throw new InvalidDataException("Unsupported existing save version.");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                if (!world.LastLoadRecovered || world.StoredPath != path) throw new InvalidDataException("Existing save is invalid; load recovery before saving.", error);
                primaryHealthy = false;
            }
        }
        var referenceHealth = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var entry in previous.Chunks)
            {
                bool healthy = VerifyFile(Path.Combine(directory, entry.File), entry.Sha256);
                referenceHealth[entry.File] = healthy;
                if (!healthy) primaryHealthy = false;
            }
        bool reuse = world.StoredPath == path && world.StoredGeneration == previous?.Generation && previous is not null;
        var references = new List<ChunkFileReference>();
        var revisions = new Dictionary<ChunkCoord, long>();
        var created = new List<string>();
        var dirtyCoordinates = world.Chunks.DirtyCoordinates.ToHashSet();
        var tombstones = dirtyCoordinates.Where(c => !world.Chunks.Chunks.ContainsKey(c)).ToDictionary(c => c, c => world.Chunks.GetContentRevision(c));
        bool committed = false;
        try
        {
            if (world.Switches.Any(cell => world.Get(cell) != Block.Lever || world.GetPersistentId(cell) != world.Registries.Blocks.GetId(Block.Lever)))
                throw new InvalidDataException("Invalid active switch state.");
            var counts = new Dictionary<string, int>(world.UnknownInventory, StringComparer.Ordinal);
            for (int i = 0; i < world.Inventory.Length; i++)
            {
                if (world.Inventory[i] < 0) throw new InvalidDataException("Negative inventory.");
                counts[world.Registries.Blocks.GetId((Block)i).ToString()] = world.Inventory[i];
            }
            var old = reuse ? previous!.Chunks.ToDictionary(e => e.Coord) : [];
            int written = 0, reused = 0;
            foreach (var (coord, chunk) in world.Chunks.Chunks.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y).ThenBy(p => p.Key.Z))
            {
                if (!coord.IsValid) throw new InvalidDataException("Chunk coordinate exceeds Cell integer range.");
                long revision = world.Chunks.GetContentRevision(coord);
                revisions.Add(coord, revision);
                string signature = SwitchSignature(world, coord);
                if (old.TryGetValue(coord, out var entry) && !chunk.IsDirty && !dirtyCoordinates.Contains(coord) &&
                    entry.SwitchSignature == signature)
                {
                    if (referenceHealth.GetValueOrDefault(entry.File))
                    { references.Add(entry); reused++; continue; }
                    // Repair from the active in-memory chunk; retain any known-good recovery manifest.
                    primaryHealthy = false;
                }
                var data = CaptureChunk(world, coord);
                string json = JsonSerializer.Serialize(data, Options);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                if (bytes.Length > MaximumChunkBytes) throw new InvalidDataException("Chunk exceeds the 32 MiB storage limit.");
                string name = $"{coord.X}_{coord.Y}_{coord.Z}-{Guid.NewGuid():N}.json";
                string file = Path.Combine(directory, name);
                AtomicSave.Write(file, json);
                created.Add(file);
                references.Add(new(coord, name, Convert.ToHexString(SHA256.HashData(bytes)), signature));
                written++;
            }
            var manifest = new ChunkWorldManifest(4, Guid.NewGuid().ToString("N"), world.Seed, world.TerrainVersion,
                world.TickCount, position, yaw, pitch, counts, references.ToArray());
            checkpoint?.Invoke(SaveCheckpoint.ChunksWritten);
            string text = JsonSerializer.Serialize(manifest, Options);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 64L * 1024 * 1024) throw new InvalidDataException("World manifest exceeds 64 MiB.");
            if (migrated) AtomicSave.BackupLegacy(path);
            checkpoint?.Invoke(SaveCheckpoint.BeforeManifestCommit);
            AtomicSave.Write(path, text, File.Exists(path) && primaryHealthy ? path + ".bak" : null);
            committed = true;
            var committedCoordinates = references.Select(e => e.Coord).ToHashSet();
            int removed = previous?.Chunks.Count(entry => !committedCoordinates.Contains(entry.Coord)) ?? 0;
            var referencesByCoord = references.ToDictionary(e => e.Coord);
            foreach (var pair in revisions)
            {
                if (SwitchSignature(world, pair.Key) == referencesByCoord[pair.Key].SwitchSignature)
                    world.Chunks.MarkSaved(pair.Key, pair.Value);
            }
            foreach (var pair in tombstones)
                if (!world.Chunks.Chunks.ContainsKey(pair.Key))
                    world.Chunks.MarkSaved(pair.Key, pair.Value);
            world.StoredPath = path; world.StoredGeneration = manifest.Generation;
            world.LastSaveReport = new(written, reused, removed, migrated);
            world.LastLoadRecovered = false;
            Cleanup(directory, manifest, path + ".bak");
        }
        finally
        {
            if (!committed)
                foreach (string file in created)
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static string SwitchSignature(World world, ChunkCoord coord) => string.Join(";", world.Switches
        .Where(cell => ChunkCoord.FromWorld(cell) == coord).OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z)
        .Select(cell => $"{cell.X},{cell.Y},{cell.Z}"));

    static ChunkFileData CaptureChunk(World world, ChunkCoord coord)
    {
        var palette = new List<string>();
        var index = new Dictionary<ContentId, int>();
        int[] cells = new int[4096];
        var origin = coord.Origin;
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            var cell = origin + new Cell(x, y, z);
            var id = world.GetPersistentId(cell);
            if (!index.TryGetValue(id, out int paletteIndex))
            { paletteIndex = palette.Count; palette.Add(id.ToString()); index.Add(id, paletteIndex); }
            cells[(y * 16 + z) * 16 + x] = paletteIndex;
        }
        var payloads = world.blockPayloads.Where(p => ChunkCoord.FromWorld(p.Key) == coord && world.Get(p.Key) != Block.Air && p.Value.Data.HasValue)
            .Select(p => new World.CellData(p.Key, p.Value.Data!.Value)).ToArray();
        return new(1, coord, palette.ToArray(), cells,
            world.Switches.Where(c => ChunkCoord.FromWorld(c) == coord).ToArray(), payloads);
    }

    internal static ChunkWorldManifest ParseManifest(JsonElement root)
    {
        var manifest = JsonSerializer.Deserialize<ChunkWorldManifest>(root.GetRawText(), Options)
            ?? throw new InvalidDataException("Empty chunk manifest.");
        World.ValidatePlayer(manifest.Position, manifest.Yaw, manifest.Pitch);
        if (manifest.Version != 4 || manifest.Generation is null || !Guid.TryParseExact(manifest.Generation, "N", out _) ||
            manifest.TerrainVersion is not (0 or 1) || manifest.TickCount < 0 || manifest.Counts is null ||
            manifest.Chunks is null || manifest.Chunks.Length > 1000000)
            throw new InvalidDataException("Invalid chunk manifest.");
        var coordinates = new HashSet<ChunkCoord>();
        foreach (var entry in manifest.Chunks)
        {
            if (entry is null || !entry.Coord.IsValid || !coordinates.Add(entry.Coord) ||
                entry.File is null || !ChunkName.IsMatch(entry.File) ||
                !entry.File.StartsWith($"{entry.Coord.X}_{entry.Coord.Y}_{entry.Coord.Z}-", StringComparison.Ordinal) ||
                entry.Sha256 is null || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)) || entry.SwitchSignature is null)
                throw new InvalidDataException("Invalid chunk file reference.");
        }
        foreach (var pair in manifest.Counts)
        { _ = new ContentId(pair.Key); if (pair.Value < 0) throw new InvalidDataException("Negative inventory."); }
        return manifest;
    }

    internal static (Vector3 Position, float Yaw, float Pitch) Load(World world, string path, JsonElement root, bool recovered)
    {
        var manifest = ParseManifest(root);
        ValidateDataDirectory(path + ".chunks");
        var manager = new ChunkManager(world.Registries.Blocks);
        var payloads = new Dictionary<Cell, World.BlockPayload>();
        var switches = new HashSet<Cell>();
        foreach (var entry in manifest.Chunks)
        {
            string file = Path.Combine(path + ".chunks", entry.File);
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaximumChunkBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Missing or oversized chunk.");
            byte[] bytes = File.ReadAllBytes(file);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chunk checksum mismatch.");
            var data = JsonSerializer.Deserialize<ChunkFileData>(bytes, Options) ?? throw new InvalidDataException("Empty chunk.");
            if (data.Version != 1 || data.Coord != entry.Coord || data.Palette is null || data.Palette.Length is < 1 or > 4096 ||
                data.Cells is null || data.Cells.Length != 4096 || data.Switches is null || data.BlockData is null)
                throw new InvalidDataException("Invalid chunk payload.");
            var ids = data.Palette.Select(s => new ContentId(s)).ToArray();
            if (ids.Distinct().Count() != ids.Length || data.Cells.Any(i => (uint)i >= ids.Length))
                throw new InvalidDataException("Invalid chunk palette.");
            var origin = data.Coord.Origin;
            for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
            {
                var cell = origin + new Cell(x, y, z);
                var id = ids[data.Cells[(y * 16 + z) * 16 + x]];
                if (!world.Registries.Blocks.TryGetBlock(id, out var block))
                { block = Block.Stone; payloads.Add(cell, new(id, null)); }
                manager.Set(cell, block);
            }
            foreach (var dataEntry in data.BlockData)
            {
                if (dataEntry is null || ChunkCoord.FromWorld(dataEntry.Cell) != data.Coord || manager.Get(dataEntry.Cell) == Block.Air ||
                    dataEntry.Data.ValueKind == JsonValueKind.Undefined ||
                    payloads.TryGetValue(dataEntry.Cell, out var prior) && prior.Data.HasValue)
                    throw new InvalidDataException("Invalid chunk block data.");
                var local = ChunkCoord.ToLocal(dataEntry.Cell);
                payloads[dataEntry.Cell] = new(ids[data.Cells[(local.Y * 16 + local.Z) * 16 + local.X]], dataEntry.Data.Clone());
            }
            foreach (var cell in data.Switches)
                if (ChunkCoord.FromWorld(cell) != data.Coord || manager.Get(cell) != Block.Lever ||
                    payloads.TryGetValue(cell, out var unknown) && !world.Registries.Blocks.TryGetBlock(unknown.Id, out _) || !switches.Add(cell))
                    throw new InvalidDataException("Invalid chunk switch state.");
            string signature = string.Join(";", data.Switches.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).Select(c => $"{c.X},{c.Y},{c.Z}"));
            if (signature != entry.SwitchSignature || !manager.Chunks.ContainsKey(data.Coord))
                throw new InvalidDataException("Empty chunk or mismatched switch signature.");
        }
        int[] inventory = new int[world.Registries.Blocks.Count];
        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in manifest.Counts)
            if (world.Registries.Blocks.TryGetBlock(new(pair.Key), out var block)) inventory[(int)block] = pair.Value;
            else missing.Add(pair.Key, pair.Value);
        world.InstallState(manager, payloads, inventory, missing, switches, manifest.Seed, manifest.TerrainVersion, manifest.TickCount);
        world.Chunks.MarkAllSaved();
        world.StoredPath = path; world.StoredGeneration = manifest.Generation;
        world.LastLoadRecovered = recovered;
        return (manifest.Position, manifest.Yaw, Math.Clamp(manifest.Pitch, -1.5f, 1.5f));
    }

    static bool VerifyFile(string file, string expectedHash)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists && info.Length <= MaximumChunkBytes && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static void ValidateDataDirectory(string directory)
    {
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked chunk directories are not supported.");
    }

    static void Cleanup(string directory, ChunkWorldManifest current, string backup)
    {
        try
        {
            var keep = current.Chunks.Select(e => e.File).ToHashSet(StringComparer.Ordinal);
            if (File.Exists(backup))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(backup));
                if (doc.RootElement.GetProperty("Version").GetInt32() == 4)
                    keep.UnionWith(ParseManifest(doc.RootElement).Chunks.Select(e => e.File));
            }
            foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (ChunkName.IsMatch(name) && !keep.Contains(name)) File.Delete(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { /* Cleanup cannot turn a successful commit into a failed save. */ }
    }
}
