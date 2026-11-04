using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

/// <summary>Chunk-file persistence, migration, failure recovery, and signed-coordinate acceptance checks.</summary>
static class ChunkPersistenceTests
{
    static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            passed++;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "blockcircuit-chunks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            TestSignedCoordinates(Check, temporary);
            TestDirtyChunks(Check, temporary);
            TestMetadataAndBlockState(Check, temporary);
            TestLegacyMigration(Check, temporary);
            TestModRoundtrip(Check, temporary);
            TestSaveFailures(Check, temporary);
            TestSaveRobustness(Check, temporary);
            TestNegativeCircuitAndSnapshot(Check, temporary);
            TestRecoveryAndValidation(Check, temporary);
        }
        finally { Directory.Delete(temporary, recursive: true); }
        Console.WriteLine($"Chunk persistence tests passed: {passed}");
    }

    static string SavePath(string directory, string name)
    {
        string child = Path.Combine(directory, name);
        Directory.CreateDirectory(child);
        return Path.Combine(child, "world.json");
    }

    static Dictionary<ChunkCoord, string> ChunkFiles(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Chunks").EnumerateArray().ToDictionary(entry =>
        {
            var coord = entry.GetProperty("Coord");
            return new ChunkCoord(coord.GetProperty("X").GetInt32(), coord.GetProperty("Y").GetInt32(), coord.GetProperty("Z").GetInt32());
        }, entry => entry.GetProperty("File").GetString()!);
    }

    static bool InvalidLoad(World world, string path)
    {
        try { world.Load(path); return false; }
        catch (InvalidDataException) { return true; }
    }

    static bool SameJson(JsonElement? actual, JsonElement expected) => actual.HasValue &&
        JsonNode.DeepEquals(JsonNode.Parse(actual.Value.GetRawText()), JsonNode.Parse(expected.GetRawText()));

    static BlockInfo Custom(string name) => new(Block.Air, name, BlockCategory.Stone, 0xA0B0C0, BlockPattern.Stone, .5f);

    static void TestSignedCoordinates(Action<bool, string> check, string directory)
    {
        int[] values = [int.MinValue, -33, -32, -17, -16, -15, -1, 0, 1, 15, 16, 17, 31, 32, int.MaxValue];
        bool coordinates = true;
        foreach (int value in values)
        {
            var cell = new Cell(value, value, value);
            ChunkCoord chunk = ChunkCoord.FromWorld(cell);
            Cell local = ChunkCoord.ToLocal(cell);
            coordinates &= local.X is >= 0 and < 16 && local.Y is >= 0 and < 16 && local.Z is >= 0 and < 16 &&
                chunk.Origin + local == cell && chunk.X == (int)Math.Floor(value / 16d);
        }
        check(coordinates, "floor division and local coordinates reconstruct signed 32-bit cells across negative chunk boundaries");

        var world = new World(false);
        Cell[] cells = [new(-1, -1, -1), new(-16, 2, -17), new(int.MinValue, 0, int.MinValue), new(int.MaxValue, 27, int.MaxValue)];
        for (int i = 0; i < cells.Length; i++) world.Set(cells[i], i % 2 == 0 ? Block.Stone : Block.Dirt);
        check(cells.Select((cell, i) => world.Get(cell) == (i % 2 == 0 ? Block.Stone : Block.Dirt)).All(value => value),
            "the world storage API retains negative and extreme integer block coordinates outside the finite play area");
        check(cells.All(cell => ChunkMesher.Build(world, ChunkCoord.FromWorld(cell)).ExposedFaceCount == 6),
            "CPU meshing retains all six exposed faces at signed integer coordinate limits without halo overflow");
        string path = SavePath(directory, "signed");
        world.Save(path, world.Spawn(), 0, 0);
        var restored = new World(false);
        restored.Load(path);
        check(cells.All(cell => restored.Get(cell) == world.Get(cell)) && restored.Chunks.Chunks.Count == cells.Length,
            "negative and extreme signed cells survive chunk-file save and load without coordinate wrapping");
    }

    static void TestDirtyChunks(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "dirty");
        var world = new World(false);
        Cell left = new(1, 2, 1), right = new(17, 2, 1), boundary = new(15, 2, 1);
        world.Set(left, Block.Stone);
        world.Set(right, Block.Dirt);
        world.Save(path, world.Spawn(), .2f, -.1f);
        var initial = ChunkFiles(path);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            check(document.RootElement.GetProperty("Version").GetInt32() == 4 && !document.RootElement.TryGetProperty("Cells", out _) && initial.Count == 2,
                "v4 world metadata references separate chunk files instead of a flattened world block array");
        check(world.LastSaveReport.WrittenChunks == 2 && world.LastSaveReport.ReusedChunks == 0,
            "the first chunk save writes exactly the two populated chunks");
        world.Save(path, world.Spawn(), .2f, -.1f);
        check(world.LastSaveReport.WrittenChunks == 0 && world.LastSaveReport.ReusedChunks == 2 &&
              ChunkFiles(path).All(pair => initial[pair.Key] == pair.Value),
            "an unchanged save reuses both existing chunk files without rewriting block data");

        world.Set(boundary, Block.Glass);
        world.Save(path, world.Spawn(), .2f, -.1f);
        var edited = ChunkFiles(path);
        check(world.LastSaveReport.WrittenChunks == 1 && world.LastSaveReport.ReusedChunks == 1 &&
              edited[new(0, 0, 0)] != initial[new(0, 0, 0)] && edited[new(1, 0, 0)] == initial[new(1, 0, 0)],
            "editing a boundary saves only its content owner while neighbor mesh invalidation does not dirty neighbor storage");
        world.Set(left, Block.Air);
        world.Set(boundary, Block.Air);
        world.Save(path, world.Spawn(), 0, 0);
        check(world.LastSaveReport.WrittenChunks == 0 && world.LastSaveReport.RemovedChunks == 1 && ChunkFiles(path).Count == 1,
            "removing a chunk's final blocks records its deletion without writing an empty replacement chunk");
        var restored = new World(false);
        restored.Load(path);
        check(restored.Get(left) == Block.Air && restored.Get(boundary) == Block.Air && restored.Get(right) == Block.Dirt && restored.Chunks.Chunks.Count == 1,
            "a deleted chunk stays empty after reloading while reused neighbor content remains intact");
        restored.Save(path, restored.Spawn(), 0, 0);
        check(restored.LastSaveReport.WrittenChunks == 0 && restored.LastSaveReport.ReusedChunks == 1,
            "freshly loaded chunks begin clean and their unchanged files can be reused by the next save");
    }

    static void TestMetadataAndBlockState(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "state");
        var world = new World(false);
        Cell lever = new(1, 1, 1), dataCell = new(17, 2, 1);
        world.Set(new(1, 0, 1), Block.Stone);
        world.Set(lever, Block.Lever);
        world.Set(dataCell, Block.Stone);
        world.Save(path, world.Spawn(), 0, 0);
        world.Toggle(lever);
        world.Save(path, world.Spawn(), 0, 0);
        check(world.LastSaveReport.WrittenChunks == 1 && world.LastSaveReport.ReusedChunks == 1,
            "a lever state change persists its owner chunk even when no block ID changed");
        using var payload = JsonDocument.Parse("{\"charge\":17,\"state\":\"ready\"}");
        world.SetBlockData(dataCell, payload.RootElement);
        world.Save(path, world.Spawn(), 0, 0);
        check(world.LastSaveReport.WrittenChunks == 1 && world.LastSaveReport.ReusedChunks == 1,
            "opaque block-data edits dirty and save the affected chunk independently of mesh revisions");
        world.Tick();
        world.Tick();
        world.Inventory[(int)Block.Stone] = 37;
        Vector3 position = new(12.5f, 4, 12.5f);
        world.Save(path, position, .3f, -.2f);
        check(world.LastSaveReport.WrittenChunks == 0 && world.LastSaveReport.ReusedChunks == 2,
            "inventory, player, and tick metadata changes reuse unchanged chunk files");
        var restored = new World(false);
        var player = restored.Load(path);
        check(restored.Switches.Contains(lever) && SameJson(restored.GetBlockData(dataCell), payload.RootElement) &&
              restored.TickCount == 2 && restored.Inventory[(int)Block.Stone] == 37 && player.Position == position && player.Yaw == .3f && player.Pitch == -.2f,
            "chunk state and separate player, inventory, and time metadata restore together");
    }

    static void TestLegacyMigration(Action<bool, string> check, string directory)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string path = SavePath(directory, "legacy-" + version);
            var original = new World(false);
            Cell cell = new(4, 2, 4);
            original.Set(cell, Block.Stone);
            original.Inventory[(int)Block.Stone] = 19;
            if (version == 1)
            {
                var data = new World.SaveData(1, original.Blocks, [], original.Inventory.Take(10).ToArray(), original.Spawn(), .2f, -.1f);
                File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            }
            else if (version == 2) original.SaveLegacy(path, original.Spawn(), .2f, -.1f);
            else original.ExportSnapshot(path, original.Spawn(), .2f, -.1f);
            byte[] before = File.ReadAllBytes(path);
            var migrated = new World(false);
            migrated.Load(path);
            migrated.Save(path, migrated.Spawn(), .2f, -.1f);
            string backups = Path.Combine(Path.GetDirectoryName(path)!, "backups");
            bool backupPreserved = Directory.Exists(backups) && Directory.GetFiles(backups)
                .Any(candidate => File.ReadAllBytes(candidate).AsSpan().SequenceEqual(before));
            check(migrated.LastSaveReport.MigratedLegacy && migrated.Get(cell) == Block.Stone &&
                  migrated.Inventory[(int)Block.Stone] == 19 && backupPreserved,
                $"v{version} migration preserves blocks and quantities and backs up the exact original save before conversion");
            var restored = new World(false);
            restored.Load(path);
            check(restored.Get(cell) == Block.Stone && restored.Inventory[(int)Block.Stone] == 19 && !restored.LastLoadRecovered,
                $"migrated v{version} content reloads from the new primary chunk store");
        }
    }

    static void TestModRoundtrip(Action<bool, string> check, string directory)
    {
        var alphaId = new ContentId("chunks:alpha");
        var betaId = new ContentId("chunks:beta");
        Block alpha = Block.Air, beta = Block.Air;
        var originalRegistry = GameRegistries.Create(registries =>
        {
            for (int i = 0; i < 90; i++) registries.Blocks.Register(new ContentId($"chunks:filler_{i}"), Custom("Filler " + i));
            alpha = registries.Blocks.Register(alphaId, Custom("Alpha"));
            beta = registries.Blocks.Register(betaId, Custom("Beta"));
        });
        Block reorderedAlpha = Block.Air, reorderedBeta = Block.Air;
        var reordered = GameRegistries.Create(registries =>
        {
            reorderedBeta = registries.Blocks.Register(betaId, Custom("Beta"));
            reorderedAlpha = registries.Blocks.Register(alphaId, Custom("Alpha"));
            for (int i = 89; i >= 0; i--) registries.Blocks.Register(new ContentId($"chunks:filler_{i}"), Custom("Filler " + i));
        });
        Cell first = new(-17, 2, -1), second = new(17, 2, 17);
        var original = new World(false, registries: originalRegistry);
        original.Set(first, alpha);
        original.Set(second, beta);
        original.Inventory[(int)alpha] = 7;
        original.Inventory[(int)beta] = 11;
        using var payload = JsonDocument.Parse("{\"charge\":29,\"facing\":\"north\"}");
        original.SetBlockData(first, payload.RootElement);
        string path = SavePath(directory, "mod");
        original.Save(path, original.Spawn(), 0, 0);
        var changedOrder = new World(false, registries: reordered);
        changedOrder.Load(path);
        check((int)alpha > 255 && alpha != reorderedAlpha && changedOrder.Get(first) == reorderedAlpha && changedOrder.Get(second) == reorderedBeta &&
              changedOrder.Inventory[(int)reorderedAlpha] == 7 && changedOrder.Inventory[(int)reorderedBeta] == 11 && SameJson(changedOrder.GetBlockData(first), payload.RootElement),
            "chunk palettes restore greater-than-byte mod IDs, negative cells, inventory, and payloads under a different Registry order");
        var absent = new World(false);
        absent.Load(path);
        check(absent.Get(first) == Block.Stone && absent.GetPersistentId(first) == alphaId &&
              absent.UnknownInventory.TryGetValue(alphaId.ToString(), out int quantity) && quantity == 7 && SameJson(absent.GetBlockData(first), payload.RootElement),
            "unavailable mods retain stable names, quantities, and opaque chunk data behind fallback blocks");
        string missingPath = SavePath(directory, "mod-absent");
        absent.Save(missingPath, absent.Spawn(), 0, 0);
        var reinstalled = new World(false, registries: originalRegistry);
        reinstalled.Load(missingPath);
        check(reinstalled.Get(first) == alpha && reinstalled.Get(second) == beta && reinstalled.Inventory[(int)alpha] == 7 &&
              reinstalled.Inventory[(int)beta] == 11 && SameJson(reinstalled.GetBlockData(first), payload.RootElement),
            "resaving a chunk world without its mods preserves enough data to restore them after reinstalling");

        string replacementPath = SavePath(directory, "mod-placeholder-replacement");
        original.Save(replacementPath, original.Spawn(), 0, 0);
        var replacement = new World(false);
        replacement.Load(replacementPath);
        replacement.Set(first, Block.Stone);
        replacement.Save(replacementPath, replacement.Spawn(), 0, 0);
        var replaced = new World(false);
        replaced.Load(replacementPath);
        check(replacement.LastSaveReport.WrittenChunks == 1 && replaced.Get(first) == Block.Stone &&
              replaced.GetPersistentId(first) == replaced.Registries.Blocks.GetId(Block.Stone) && replaced.GetBlockData(first) is null,
            "explicitly replacing a missing-mod fallback with Stone saves its changed identity even when the runtime block is unchanged");

        string removedPath = SavePath(directory, "mod-direct-remove");
        original.Save(removedPath, original.Spawn(), 0, 0);
        var removed = new World(false);
        removed.Load(removedPath);
        removed.Chunks.Remove(ChunkCoord.FromWorld(first));
        Cell neighbor = first + new Cell(-1, 0, 0);
        removed.Set(neighbor, Block.Dirt);
        removed.Save(removedPath, removed.Spawn(), 0, 0);
        var removedRestored = new World(false);
        removedRestored.Load(removedPath);
        check(removedRestored.Get(first) == Block.Air && removedRestored.GetBlockData(first) is null &&
              removedRestored.GetPersistentId(first) == removedRestored.Registries.Blocks.GetId(Block.Air) && removedRestored.Get(neighbor) == Block.Dirt,
            "removing and recreating a chunk cannot resurrect the removed mod block's stale identifier or payload");

        string directPath = SavePath(directory, "mod-direct-set");
        original.Save(directPath, original.Spawn(), 0, 0);
        var direct = new World(false);
        direct.Load(directPath);
        direct.Chunks.Set(first, Block.Dirt);
        direct.Save(directPath, direct.Spawn(), 0, 0);
        var directRestored = new World(false);
        directRestored.Load(directPath);
        check(directRestored.Get(first) == Block.Dirt && directRestored.GetPersistentId(first) == directRestored.Registries.Blocks.GetId(Block.Dirt) &&
              directRestored.GetBlockData(first) is null,
            "direct chunk edits invalidate stale missing-mod identities and opaque data before saving the replacement block");
    }

    static void TestSaveFailures(Action<bool, string> check, string directory)
    {
        foreach (SaveCheckpoint failurePoint in new[] { SaveCheckpoint.ChunksWritten, SaveCheckpoint.BeforeManifestCommit })
        {
            string path = SavePath(directory, "failure-" + failurePoint);
            var world = new World(false);
            Cell cell = new(4, 2, 4);
            world.Set(cell, Block.Stone);
            world.Save(path, world.Spawn(), 0, 0);
            byte[] committed = File.ReadAllBytes(path);
            world.Set(cell, Block.Dirt);
            bool failed = false;
            try
            {
                ChunkSaveStore.Save(world, path, world.Spawn(), 0, 0, checkpoint =>
                {
                    if (checkpoint == failurePoint) throw new IOException("Injected chunk save failure");
                });
            }
            catch (IOException) { failed = true; }
            var preserved = new World(false);
            preserved.Load(path);
            check(failed && File.ReadAllBytes(path).AsSpan().SequenceEqual(committed) && preserved.Get(cell) == Block.Stone && world.Get(cell) == Block.Dirt,
                $"failure at {failurePoint} preserves the previous committed world and current in-memory edits");
            world.Save(path, world.Spawn(), 0, 0);
            var retried = new World(false);
            retried.Load(path);
            check(world.LastSaveReport.WrittenChunks == 1 && retried.Get(cell) == Block.Dirt,
                $"failure at {failurePoint} leaves content dirty so retry commits the edited chunk");
        }
    }

    static void TestRecoveryAndValidation(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "recovery");
        Cell cell = new(4, 2, 4), marker = new(7, 2, 7);
        var world = new World(false);
        world.Set(cell, Block.Stone);
        world.Save(path, world.Spawn(), 0, 0);
        world.Set(cell, Block.Dirt);
        world.Save(path, world.Spawn(), 0, 0);
        File.WriteAllText(path, "{invalid");
        var restored = new World(false);
        restored.Load(path);
        check(restored.LastLoadRecovered && restored.Get(cell) == Block.Stone,
            "a corrupt primary manifest recovers the previous complete generation and its immutable chunk files");
        restored.Set(marker, Block.Glass);
        File.WriteAllText(path + ".bak", "{invalid");
        check(InvalidLoad(restored, path) && restored.Get(cell) == Block.Stone && restored.Get(marker) == Block.Glass,
            "corrupt primary and backup manifests are rejected without replacing the active world");

        string checksumPath = SavePath(directory, "checksum");
        var checksumSource = new World(false);
        checksumSource.Set(cell, Block.Stone);
        checksumSource.Save(checksumPath, checksumSource.Spawn(), 0, 0);
        string chunkPath = Path.Combine(checksumPath + ".chunks", ChunkFiles(checksumPath).Single().Value);
        File.AppendAllText(chunkPath, " ");
        if (File.Exists(checksumPath + ".bak")) File.Delete(checksumPath + ".bak");
        check(InvalidLoad(restored, checksumPath) && restored.Get(marker) == Block.Glass,
            "chunk checksum mismatches are rejected before any active world state changes");

        string traversalPath = SavePath(directory, "traversal");
        var traversalSource = new World(false);
        traversalSource.Set(cell, Block.Stone);
        traversalSource.Save(traversalPath, traversalSource.Spawn(), 0, 0);
        string valid = File.ReadAllText(traversalPath);
        string validChunk = Path.Combine(traversalPath + ".chunks", ChunkFiles(traversalPath).Single().Value);
        string outside = Path.Combine(Path.GetDirectoryName(traversalPath)!, "escape.json");
        byte[] outsideBytes = File.ReadAllBytes(validChunk);
        File.WriteAllBytes(outside, outsideBytes);
        bool pathsRejected = true;
        foreach (string unsafeName in new[] { "../escape.json", outside })
        {
            var invalid = JsonNode.Parse(valid)!;
            invalid["Chunks"]!.AsArray()[0]!["File"] = unsafeName;
            File.WriteAllText(traversalPath, invalid.ToJsonString());
            if (File.Exists(traversalPath + ".bak")) File.Delete(traversalPath + ".bak");
            pathsRejected &= InvalidLoad(restored, traversalPath) && restored.Get(marker) == Block.Glass &&
                File.ReadAllBytes(outside).AsSpan().SequenceEqual(outsideBytes);
        }
        check(pathsRejected,
            "chunk manifest entries reject relative traversal and absolute filenames even when the external file has a matching checksum");
    }

    static void TestSaveRobustness(Action<bool, string> check, string directory)
    {
        Cell cell = new(4, 2, 4);
        string repairPath = SavePath(directory, "save-repairs-clean-file");
        var repair = new World(false);
        repair.Set(cell, Block.Stone);
        repair.Save(repairPath, repair.Spawn(), 0, 0);
        string oldFile = ChunkFiles(repairPath).Single().Value;
        File.AppendAllText(Path.Combine(repairPath + ".chunks", oldFile), " ");
        repair.Save(repairPath, repair.Spawn(), 0, 0);
        var repaired = new World(false);
        repaired.Load(repairPath);
        check(repair.LastSaveReport.WrittenChunks == 1 && repair.LastSaveReport.ReusedChunks == 0 &&
              ChunkFiles(repairPath).Single().Value != oldFile && repaired.Get(cell) == Block.Stone,
            "saving checks clean referenced files and rewrites a corrupt chunk from the valid in-memory contents");

        string recoveredPath = SavePath(directory, "save-after-chunk-recovery");
        var original = new World(false);
        original.Set(cell, Block.Stone);
        original.Save(recoveredPath, original.Spawn(), 0, 0);
        original.Set(cell, Block.Dirt);
        original.Save(recoveredPath, original.Spawn(), 0, 0);
        byte[] goodBackup = File.ReadAllBytes(recoveredPath + ".bak");
        string corruptFile = ChunkFiles(recoveredPath).Single().Value;
        File.AppendAllText(Path.Combine(recoveredPath + ".chunks", corruptFile), " ");
        var recovered = new World(false);
        recovered.Load(recoveredPath);
        check(recovered.LastLoadRecovered && recovered.Get(cell) == Block.Stone,
            "a checksum failure in the newest generation recovers the complete older chunk generation");
        recovered.Save(recoveredPath, recovered.Spawn(), 0, 0);
        bool preservedBackup = File.ReadAllBytes(recoveredPath + ".bak").AsSpan().SequenceEqual(goodBackup);
        File.WriteAllText(recoveredPath, "{invalid");
        var recoveredAgain = new World(false);
        recoveredAgain.Load(recoveredPath);
        check(preservedBackup && recoveredAgain.LastLoadRecovered && recoveredAgain.Get(cell) == Block.Stone,
            "saving a recovered world keeps the good backup instead of replacing it with a manifest that references corrupt chunks");

        string dirtyPath = SavePath(directory, "dirty-save-preserves-good-backup");
        var dirty = new World(false);
        dirty.Set(cell, Block.Stone);
        dirty.Save(dirtyPath, dirty.Spawn(), 0, 0);
        dirty.Set(cell, Block.Dirt);
        dirty.Save(dirtyPath, dirty.Spawn(), 0, 0);
        byte[] dirtyBackup = File.ReadAllBytes(dirtyPath + ".bak");
        string dirtyFile = ChunkFiles(dirtyPath).Single().Value;
        File.AppendAllText(Path.Combine(dirtyPath + ".chunks", dirtyFile), " ");
        dirty.Set(cell, Block.Grass);
        dirty.Save(dirtyPath, dirty.Spawn(), 0, 0);
        bool dirtyBackupPreserved = File.ReadAllBytes(dirtyPath + ".bak").AsSpan().SequenceEqual(dirtyBackup);
        File.WriteAllText(dirtyPath, "{invalid");
        var dirtyRecovered = new World(false);
        dirtyRecovered.Load(dirtyPath);
        check(dirtyBackupPreserved && dirtyRecovered.LastLoadRecovered && dirtyRecovered.Get(cell) == Block.Stone,
            "a dirty rewrite validates all prior chunk references before deciding whether the prior manifest may replace the good backup");

        string concurrentPath = SavePath(directory, "edit-during-save");
        var edited = new World(false);
        edited.Set(cell, Block.Stone);
        edited.Save(concurrentPath, edited.Spawn(), 0, 0);
        ChunkSaveStore.Save(edited, concurrentPath, edited.Spawn(), 0, 0, checkpoint =>
        {
            if (checkpoint == SaveCheckpoint.ChunksWritten) edited.Set(cell, Block.Dirt);
        });
        var captured = new World(false);
        captured.Load(concurrentPath);
        ChunkCoord coord = ChunkCoord.FromWorld(cell);
        check(captured.Get(cell) == Block.Stone && edited.Get(cell) == Block.Dirt && edited.Chunks.Chunks[coord].IsDirty &&
              edited.Chunks.DirtyCoordinates.Contains(coord),
            "an edit after chunk capture cannot change the committed snapshot or receive a stale clean acknowledgement");
        edited.Save(concurrentPath, edited.Spawn(), 0, 0);
        captured.Load(concurrentPath);
        check(edited.LastSaveReport.WrittenChunks == 1 && captured.Get(cell) == Block.Dirt && !edited.Chunks.Chunks[coord].IsDirty,
            "the next save persists an edit that arrived after the prior chunk capture");

        ChunkSaveStore.Save(edited, concurrentPath, edited.Spawn(), 0, 0, checkpoint =>
        {
            if (checkpoint == SaveCheckpoint.ChunksWritten) edited.Set(cell, Block.Air);
        });
        captured.Load(concurrentPath);
        check(captured.Get(cell) == Block.Dirt && edited.Get(cell) == Block.Air && edited.Chunks.DirtyCoordinates.Contains(coord),
            "removing a chunk after capture retains its deletion tombstone until a matching generation commits it");
        edited.Save(concurrentPath, edited.Spawn(), 0, 0);
        captured.Load(concurrentPath);
        check(captured.Get(cell) == Block.Air && edited.LastSaveReport.RemovedChunks == 1 && !edited.Chunks.DirtyCoordinates.Contains(coord),
            "a later save commits the captured-time deletion without prematurely clearing its tombstone");
    }

    static void TestNegativeCircuitAndSnapshot(Action<bool, string> check, string directory)
    {
        var world = new World(false);
        Cell lever = new(-17, 1, -1), lamp = new(-14, 1, -1);
        for (int x = -17; x <= -14; x++) world.Set(new(x, 0, -1), Block.Stone);
        world.Set(lever, Block.Lever);
        world.Set(new(-16, 1, -1), Block.Wire);
        world.Set(new(-15, 1, -1), Block.Wire);
        world.Set(lamp, Block.Lamp);
        world.Toggle(lever);
        using var data = JsonDocument.Parse("{\"mode\":\"pulse\",\"charge\":8}");
        world.SetBlockData(lamp, data.RootElement);
        string chunkPath = SavePath(directory, "negative-circuit");
        world.Save(chunkPath, world.Spawn(), 0, 0);
        var chunkRestored = new World(false);
        chunkRestored.Load(chunkPath);
        string snapshotPath = Path.Combine(Path.GetDirectoryName(chunkPath)!, "portable.json");
        world.ExportSnapshot(snapshotPath, world.Spawn(), 0, 0);
        var portable = new World(false);
        portable.Load(snapshotPath);
        check(world.Power.GetValueOrDefault(lamp) == 12 && chunkRestored.Switches.Contains(lever) &&
              chunkRestored.Power.GetValueOrDefault(lamp) == 12 && SameJson(chunkRestored.GetBlockData(lamp), data.RootElement) &&
              portable.Switches.Contains(lever) && portable.Power.GetValueOrDefault(lamp) == 12 && SameJson(portable.GetBlockData(lamp), data.RootElement),
            "negative-coordinate circuits crossing a chunk boundary retain power, lever state, and block data through chunk and portable snapshot saves");
    }
}
