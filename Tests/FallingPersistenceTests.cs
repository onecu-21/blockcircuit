using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

/// <summary>Actual world-save integration for in-flight and queued block gravity.</summary>
static class FallingPersistenceTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-falling-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestMidFall(Check, directory);
            TestQueued(Check, directory);
            TestMalformed(Check, directory);
            TestMissingMod(Check, directory);
            TestStreamingRestore(Check, directory);
            TestNewWorldBackup(Check, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Falling persistence tests passed: {passed}");
    }
    static string SavePath(string directory, string name)
    {
        string child = Path.Combine(directory, name); Directory.CreateDirectory(child);
        return Path.Combine(child, "world.json");
    }
    static void Step(World world, int ticks = 100) { for (int i = 0; i < ticks; i++) world.Tick(); }
    static int Count(World world, Block block) => world.OccupiedCells().Count(c => world.Get(c) == block) +
        world.FallingBlocks.Active.Count(a => a.Block == block);
    static bool SameData(JsonElement? actual, JsonElement expected) => actual.HasValue &&
        JsonNode.DeepEquals(JsonNode.Parse(actual.Value.GetRawText()), JsonNode.Parse(expected.GetRawText()));
    static World Source(Block block = Block.Sand, GameRegistries? registries = null)
    {
        var world = new World(false, registries: registries);
        world.Set(new(4, 0, 4), Block.Stone);
        world.Set(new(4, 7, 4), block);
        return world;
    }
    static void TestMidFall(Action<bool, string> check, string directory)
    {
        foreach (bool snapshot in new[] { false, true })
        {
            var source = Source();
            using var data = JsonDocument.Parse("{\"kind\":\"falling\",\"contents\":[2,true,null]}");
            source.SetBlockData(new(4, 7, 4), data.RootElement);
            source.Tick();
            Guid identity = source.FallingBlocks.Active.Single().Id;
            Vector3 position = source.FallingBlocks.Active.Single().Position;
            int quantity = source.Inventory[(int)Block.Sand];
            string path = SavePath(directory, snapshot ? "snapshot" : "chunks");
            if (snapshot) source.ExportSnapshot(path, source.Spawn(), .25f, -.1f);
            else source.Save(path, source.Spawn(), .25f, -.1f);
            var restored = new World(false);
            restored.Load(path);
            check(restored.FallingBlocks.Active.Count == 1 && restored.FallingBlocks.Active[0].Id == identity &&
                restored.FallingBlocks.Active[0].Position == position && restored.Get(new(4, 7, 4)) == Block.Air &&
                SameData(restored.FallingBlocks.Active[0].Data, data.RootElement),
                $"{(snapshot ? "v3 portable" : "v4 chunk")} saves restore a single mid-fall block with its identity, motion and payload");
            Step(restored);
            check(restored.FallingBlocks.Active.Count == 0 && restored.Get(new(4, 1, 4)) == Block.Sand &&
                Count(restored, Block.Sand) == 1 && restored.Inventory[(int)Block.Sand] == quantity &&
                SameData(restored.GetBlockData(new(4, 1, 4)), data.RootElement),
                $"{(snapshot ? "v3 portable" : "v4 chunk")} restored falls settle once with unchanged inventory and original block data");
        }
    }
    static void TestQueued(Action<bool, string> check, string directory)
    {
        var original = Source(Block.Gravel);
        string path = SavePath(directory, "pending");
        original.Save(path, original.Spawn(), 0, 0);
        var restored = new World(false); restored.Load(path);
        check(restored.FallingBlocks.Active.Count == 0 && restored.FallingBlocks.PendingCount > 0 &&
            restored.Get(new(4, 7, 4)) == Block.Gravel,
            "saving before the first gravity tick restores a queued source without prematurely extracting it");
        Step(restored);
        check(restored.Get(new(4, 1, 4)) == Block.Gravel && Count(restored, Block.Gravel) == 1,
            "restored scheduled gravity wakes and settles exactly once");
    }
    static void TestMalformed(Action<bool, string> check, string directory)
    {
        var original = Source(); original.Tick();
        string path = SavePath(directory, "invalid"); original.Save(path, original.Spawn(), 0, 0);
        string valid = File.ReadAllText(path);
        var target = new World(false); target.Load(path);
        Cell marker = new(9, 2, 9); target.Set(marker, Block.Dirt);
        Guid actor = target.FallingBlocks.Active.Single().Id;
        int[] inventory = target.Inventory.ToArray();
        bool atomic = true;
        foreach (string corruption in new[] { "velocity", "identity", "null-active", "bad-pending" })
        {
            var node = JsonNode.Parse(valid)!.AsObject();
            var falling = node["FallingBlocks"]!.AsObject();
            switch (corruption)
            {
                case "velocity": falling["Active"]![0]!["VelocityY"] = -99; break;
                case "identity": falling["Active"]![0]!["Identifier"] = "invalid name"; break;
                case "null-active": falling["Active"] = null; break;
                case "bad-pending": falling["Pending"] = new JsonArray(new JsonObject { ["X"] = 4, ["Y"] = -1, ["Z"] = 4 }); break;
            }
            File.WriteAllText(path, node.ToJsonString());
            bool rejected = false;
            try { target.Load(path); } catch (InvalidDataException) { rejected = true; }
            atomic &= rejected && target.Get(marker) == Block.Dirt && target.FallingBlocks.Active.Count == 1 &&
                target.FallingBlocks.Active[0].Id == actor && target.Inventory.SequenceEqual(inventory);
        }
        check(atomic, "malformed falling metadata is rejected before replacing blocks, inventory or an active falling actor");
    }
    static void TestMissingMod(Action<bool, string> check, string directory)
    {
        Block custom = Block.Air;
        var id = new ContentId("heavy:ore");
        var registry = GameRegistries.Create(r => custom = r.Blocks.Register(id,
            new(Block.Air, "Heavy Ore", BlockCategory.Ores, 0x776655, BlockPattern.Ore, AffectedByGravity: true)));
        var source = Source(custom, registry);
        source.Inventory[(int)custom] = 7;
        using var payload = JsonDocument.Parse("{\"charge\":23,\"owner\":\"factory\"}");
        source.SetBlockData(new(4, 7, 4), payload.RootElement);
        source.Tick();
        string first = SavePath(directory, "mod-installed"), second = SavePath(directory, "mod-missing");
        source.Save(first, source.Spawn(), 0, 0);
        var missing = new World(false); missing.Load(first);
        Vector3 paused = missing.FallingBlocks.Active.Single().Position;
        Step(missing, 10);
        check(missing.FallingBlocks.Active.Count == 1 && missing.FallingBlocks.Active[0].PersistentId == id &&
            missing.FallingBlocks.Active[0].Position == paused && missing.UnknownInventory.GetValueOrDefault(id.ToString()) == 7 &&
            SameData(missing.FallingBlocks.Active[0].Data, payload.RootElement),
            "a world loaded without a falling block's mod suspends it while retaining named inventory and payload");
        missing.Save(second, missing.Spawn(), 0, 0);
        var reinstalled = new World(false, registries: registry); reinstalled.Load(second);
        Step(reinstalled);
        check(reinstalled.FallingBlocks.Active.Count == 0 && reinstalled.Get(new(4, 1, 4)) == custom &&
            Count(reinstalled, custom) == 1 && reinstalled.Inventory[(int)custom] == 7 &&
            SameData(reinstalled.GetBlockData(new(4, 1, 4)), payload.RootElement),
            "resaving without a mod and reinstalling it restores and settles the original falling block exactly once");
    }
    static StreamingOptions Options() => new() { MaxLoadJobs = 1, MaxIntegrationsPerFrame = 1, MaxUnloadsPerFrame = 1, MaxResidentColumns = 8 };
    static void TestStreamingRestore(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "streamed-fall");
        var source = new World(false);
        source.Set(new(-3, 0, -3), Block.Stone);
        source.Set(new(-3, 20, -3), Block.RedSand);
        source.EnableStreaming();
        using (var initial = new WorldStreamer(source, path, Options()))
        {
            source.Tick();
            source.Save(path, new(-2.5f, 10, -2.5f), 0, 0);
        }
        Guid actor = source.FallingBlocks.Active.Single().Id;
        var restored = new World(false); restored.Load(path);
        Vector3 paused = restored.FallingBlocks.Active.Single().Position;
        Step(restored, 10);
        check(restored.IsStreaming && restored.Chunks.Chunks.Count == 0 && restored.FallingBlocks.Active.Count == 1 &&
            restored.FallingBlocks.Active[0].Id == actor && restored.FallingBlocks.Active[0].Position == paused,
            "streamed world metadata retains an active fall while its terrain column is still unloaded");
        using var streamer = new WorldStreamer(restored, path, Options());
        var timer = Stopwatch.StartNew();
        Vector3 player = new(-2.5f, 10, -2.5f);
        while (!streamer.LoadedColumns.Contains(new(-1, -1)))
        {
            streamer.Update(player, 0); restored.Tick(); Thread.Sleep(1);
            if (timer.ElapsedMilliseconds > 15000) throw new InvalidOperationException("Falling restore terrain timeout: " + streamer.LastError);
        }
        Step(restored, 150);
        check(restored.FallingBlocks.Active.Count == 0 && restored.Get(new(-3, 1, -3)) == Block.RedSand &&
            Count(restored, Block.RedSand) == 1,
            "loading a saved column resumes its paused fall across the Y16 boundary and lands only one block");
    }
    static void TestNewWorldBackup(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "new-world-backup");
        var original = Source();
        original.EnableStreaming();
        using (var initial = new WorldStreamer(original, path, Options()))
        {
            original.Tick();
            original.Save(path, new(4.5f, 10, 4.5f), 0, 0);
        }
        Guid actor = original.FallingBlocks.Active.Single().Id;
        var result = WorldSession.Create(original, new(4.5f, 10, 4.5f), 0, 0, 91, legacyTerrain: false, path);
        var backup = new World(false); backup.Load(result.BackupPath);
        check(backup.FallingBlocks.Active.Count == 1 && backup.FallingBlocks.Active[0].Id == actor && backup.IsStreaming,
            "creating a new world leaves a portable backup of the previous streamed world's in-flight block");
        using (var restored = new WorldStreamer(backup, Path.Combine(Path.GetDirectoryName(path)!, "resumed.json"), Options()))
            Step(backup);
        check(backup.Get(new(4, 1, 4)) == Block.Sand && Count(backup, Block.Sand) == 1 &&
            original.FallingBlocks.Active.Count == 1,
            "the new-world backup resumes its fall independently without mutating the original session");
    }
}
