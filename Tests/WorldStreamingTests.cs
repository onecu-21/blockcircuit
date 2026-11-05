using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;

namespace BlockCircuit;

/// <summary>Budgeted asynchronous chunk loading, revisits, save integration, and cancellation.</summary>
static class WorldStreamingTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestPriorityAndBudgets(Check, directory);
            TestSeededRevisit(Check, directory);
            TestModifiedAndEmptyRevisit(Check, directory);
            TestReloadWithUnloadedColumns(Check, directory);
            TestCancellationAndResidency(Check, directory);
            TestUnavailableMutationGuard(Check, directory);
            TestAdjacentTravelAtCapacity(Check, directory);
            TestOutOfHeightReferenceRetention(Check, directory);
            TestStreamingMetadataValidation(Check, directory);
            TestAutomaticSaveOrientation(Check, directory);
            TestLoadInvalidatesStreamingOwner(Check, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"World streaming tests passed: {passed}");
    }

    static StreamingOptions Options(int jobs = 2, int maximumColumns = 16) => new()
    {
        MaxLoadJobs = jobs,
        MaxIntegrationsPerFrame = 1,
        MaxUnloadsPerFrame = 1,
        MaxSaveCapturesPerFrame = 2,
        UnloadMargin = 1,
        MaxResidentColumns = maximumColumns
    };

    static string SavePath(string directory, string name)
    {
        string child = Path.Combine(directory, name);
        Directory.CreateDirectory(child);
        return Path.Combine(child, "world.json");
    }

    static bool Loaded(WorldStreamer streamer, int x, int z) => streamer.LoadedColumns.Contains(new ColumnCoord(x, z));
    static Vector3 Position(int x, int z) => new(x * 16 + 8.5f, 10, z * 16 + 8.5f);

    static void PumpUntil(World world, WorldStreamer streamer, Vector3 position, int radius, Func<bool> complete, string description, float yaw = 0, float pitch = 0)
    {
        var timer = Stopwatch.StartNew();
        while (!complete())
        {
            streamer.Update(position, radius, yaw, pitch);
            world.Tick();
            if (timer.ElapsedMilliseconds > 15000)
                throw new InvalidOperationException("Streaming timeout: " + description + "; last error: " + streamer.LastError);
            Thread.Sleep(1);
        }
    }

    static Block[] CaptureColumn(World world, int x, int z)
    {
        var cells = new Block[16 * 64 * 16];
        int index = 0;
        for (int y = 0; y < 64; y++)
        for (int dz = 0; dz < 16; dz++)
        for (int dx = 0; dx < 16; dx++)
            cells[index++] = world.Get(new(x * 16 + dx, y, z * 16 + dz));
        return cells;
    }

    static void TestPriorityAndBudgets(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, SavePath(directory, "priority"), Options(jobs: 1, maximumColumns: 64));
        Vector3 position = Position(0, 0);
        streamer.Update(position, 2);
        check(world.IsStreaming && world.TerrainVersion == 2 && streamer.PendingJobs <= 1 && streamer.GenerationJobsStarted <= 1,
            "streaming worlds start the versioned generator within the configured single-worker budget");
        PumpUntil(world, streamer, position, 2, () => streamer.ResidentColumnCount != 0, "initial nearest column");
        check(Loaded(streamer, 0, 0) && streamer.ResidentColumnCount == 1,
            "a single-worker loading queue publishes the player column before more distant columns");
        check(world.IsCellAvailable(new(8, 1, 8)) && world.IsCellAvailable(new(8, 100, 8)) && world.IsCellAvailable(new(8, 257, 8)),
            "a fully loaded terrain column makes its upper empty air and player headroom available");
        int before = streamer.ResidentColumnCount;
        streamer.Update(position, 2);
        check(streamer.ResidentColumnCount - before <= 1 && streamer.PendingJobs <= 1,
            "each owner-thread update respects both its integration and worker budgets");
    }

    static void TestSeededRevisit(Action<bool, string> check, string directory)
    {
        const int seed = 71423;
        var world = World.CreateStreaming(seed);
        string path = SavePath(directory, "deterministic");
        using var streamer = new WorldStreamer(world, path, Options());
        PumpUntil(world, streamer, Position(-2, -1), 0, () => Loaded(streamer, -2, -1), "negative initial column");
        Block[] original = CaptureColumn(world, -2, -1);
        check(original.Contains(Block.Bedrock) && world.IsCellAvailable(new(-24, 1, -8)),
            "negative player coordinates load a complete terrain column across signed chunk addresses");

        PumpUntil(world, streamer, Position(4, 0), 0,
            () => Loaded(streamer, 4, 0) && !Loaded(streamer, -2, -1), "leave the negative column");
        check(world.Chunks.Chunks.Keys.All(coord => coord.X != -2 || coord.Z != -1) && streamer.UnloadedColumns > 0,
            "far columns release their resident chunk payloads after loading the new player area");
        PumpUntil(world, streamer, Position(-2, -1), 0, () => Loaded(streamer, -2, -1), "negative column revisit");
        check(original.AsSpan().SequenceEqual(CaptureColumn(world, -2, -1)),
            "an unmodified column returns with identical terrain after unloading and revisiting");

        var independent = World.CreateStreaming(seed);
        using var independentStreamer = new WorldStreamer(independent, SavePath(directory, "deterministic-independent"), Options());
        PumpUntil(independent, independentStreamer, Position(-2, -1), 0, () => Loaded(independentStreamer, -2, -1), "independent seeded column");
        check(original.AsSpan().SequenceEqual(CaptureColumn(independent, -2, -1)),
            "generation of the same seed and negative column is independent of earlier loading order");
    }

    static void TestModifiedAndEmptyRevisit(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, SavePath(directory, "modified"), Options());
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "modified origin load");
        Cell marker = new(3, 30, 3);
        world.Set(marker, Block.RedWool);
        PumpUntil(world, streamer, Position(5, 0), 0, () => Loaded(streamer, 5, 0) && !Loaded(streamer, 0, 0), "modified column unload");
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "modified column reload");
        check(world.Get(marker) == Block.RedWool,
            "modified blocks are saved before their column unloads and restored on re-entry");

        // Clear the entire generated column, including every vertical chunk. This must
        // remain an explicitly empty explored column rather than being generated again.
        for (int y = 0; y < 64; y++)
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++) world.Set(new(x, y, z), Block.Air);
        world.Tick();
        PumpUntil(world, streamer, Position(5, 0), 0, () => Loaded(streamer, 5, 0) && !Loaded(streamer, 0, 0), "cleared column unload");
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "cleared column reload");
        check(CaptureColumn(world, 0, 0).All(block => block == Block.Air) &&
              world.Chunks.Chunks.Keys.All(coord => coord.X != 0 || coord.Z != 0) && world.IsCellAvailable(new(3, 10, 3)),
            "fully deleted generated chunks remain known empty on re-entry instead of regenerating their original terrain");
    }

    static void TestReloadWithUnloadedColumns(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "reload");
        var world = World.CreateStreaming(421);
        using (var streamer = new WorldStreamer(world, path, Options()))
        {
            PumpUntil(world, streamer, Position(-3, 1), 0, () => Loaded(streamer, -3, 1), "persistent negative column");
            world.Set(new(-44, 30, 20), Block.BlueWool);
            PumpUntil(world, streamer, Position(4, 1), 0,
                () => Loaded(streamer, 4, 1) && !Loaded(streamer, -3, 1), "persistent column unload");
            world.Set(new(68, 30, 20), Block.Glass);
            streamer.FlushSaves(Position(4, 1), .3f, -.2f);
        }
        var restored = new World(false);
        var player = restored.Load(path);
        check(restored.IsStreaming && restored.TerrainVersion == 2 && restored.Chunks.Chunks.Count == 0 &&
              player.Position == Position(4, 1) && player.Yaw == .3f && player.Pitch == -.2f,
            "streaming save metadata restores player state without loading every saved terrain chunk eagerly");
        using var reopened = new WorldStreamer(restored, path, Options());
        PumpUntil(restored, reopened, Position(-3, 1), 0, () => Loaded(reopened, -3, 1), "reopened formerly unloaded column");
        check(restored.Get(new(-44, 30, 20)) == Block.BlueWool,
            "saving while a modified column is unloaded retains its disk references across application reload");
        PumpUntil(restored, reopened, Position(4, 1), 0, () => Loaded(reopened, 4, 1), "reopened current column");
        check(restored.Get(new(68, 30, 20)) == Block.Glass,
            "resident and previously unloaded edits both survive the same streamed-world save");
    }

    static void TestCancellationAndResidency(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        var streamer = new WorldStreamer(world, SavePath(directory, "cancellation"), Options(jobs: 2, maximumColumns: 16));
        try
        {
            bool bounded = true;
            for (int i = 0; i < 100; i++)
            {
                streamer.Update(Position(i % 2 == 0 ? i * 5 : -i * 5, i % 3), 0);
                bounded &= streamer.PendingJobs <= 2 && streamer.ResidentColumnCount <= 16;
            }
            check(bounded, "rapid teleports keep actual background workers and resident columns within their configured caps");
            PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "return after rapid teleports");
            for (int i = 1; i <= 10; i++)
            {
                int x = i * 4;
                PumpUntil(world, streamer, Position(x, 0), 0, () => Loaded(streamer, x, 0), "bounded repeated travel");
                bounded &= streamer.ResidentColumnCount <= 16 && streamer.PendingJobs <= 2;
            }
            check(bounded && streamer.MaximumResidentColumns <= 16 && streamer.UnloadedColumns > 0,
                "repeated long-distance travel bounds resident column counts and unloads old chunk payloads");
            check(streamer.CancelledJobs > 0,
                "moving away from requested columns cancels obsolete generation work");
            streamer.Update(Position(-600, 0), 0);
            streamer.Dispose();
            int chunksAfterDispose = world.Chunks.Chunks.Count;
            Thread.Sleep(50);
            check(streamer.PendingJobs == 0 && world.Chunks.Chunks.Count == chunksAfterDispose,
                "disposing a streamer drains its workers and prevents late jobs from publishing into the world");
        }
        finally { streamer.Dispose(); }
    }
    static bool ThrowsInvalidOperation(Action operation)
    {
        try { operation(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    static void TestUnavailableMutationGuard(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, SavePath(directory, "edit-frontier"), Options());
        streamer.Update(Position(0, 0), 0);
        Cell unavailable = new(165, 5, 5);
        check(ThrowsInvalidOperation(() => world.Set(unavailable, Block.RedWool)) &&
              ThrowsInvalidOperation(() => world.Chunks.Set(unavailable, Block.BlueWool)) &&
              world.Get(unavailable) == Block.Air && !world.Chunks.Chunks.ContainsKey(ChunkCoord.FromWorld(unavailable)),
            "active streaming ownership rejects world and low-level chunk edits before their saved terrain is available");
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "editable loaded column");
        Cell loaded = new(3, 30, 3);
        world.Set(loaded, Block.RedWool);
        world.Chunks.Set(loaded, Block.BlueWool);
        check(world.Get(loaded) == Block.BlueWool,
            "both edit paths resume normally once the owning streamed column is loaded");
    }

    static void TestAdjacentTravelAtCapacity(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, SavePath(directory, "one-resident"), Options(jobs: 1, maximumColumns: 1));
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "capacity-one initial column");
        PumpUntil(world, streamer, Position(1, 0), 0, () => Loaded(streamer, 1, 0) && !Loaded(streamer, 0, 0),
            "adjacent travel with retention margin and one resident slot");
        check(streamer.ResidentColumnCount == 1 && streamer.PendingJobs <= 1 && streamer.UnloadedColumns > 0,
            "capacity pressure can evict the previous adjacent column instead of deadlocking inside the unload margin");
    }

    static void TestOutOfHeightReferenceRetention(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "out-of-height");
        var source = World.CreateStreaming(2026);
        Cell high = new(3, 1000, 3), low = new(3, 30, 3);
        source.Set(high, Block.RedWool);
        source.Set(low, Block.BlueWool);
        source.Save(path, Position(0, 0), 0, 0);
        var loaded = new World(false);
        loaded.Load(path);
        using var streamer = new WorldStreamer(loaded, path, Options());
        PumpUntil(loaded, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "bounded vertical reference load");
        check(loaded.Get(low) == Block.BlueWool && loaded.Get(high) == Block.Air &&
              loaded.Chunks.Chunks.Keys.All(coord => coord.Y is >= 0 and <= 15) &&
              loaded.StoredChunkReferences.ContainsKey(ChunkCoord.FromWorld(high)),
            "streaming loads only the playable vertical range while retaining higher legacy storage references on disk");
        streamer.FlushSaves(Position(0, 0), 0, 0);
        string exported = Path.Combine(Path.GetDirectoryName(path)!, "complete-portable.json");
        loaded.ExportSnapshot(exported, Position(0, 0), 0, 0);
        var complete = new World(false);
        complete.Load(exported);
        check(complete.Get(high) == Block.RedWool && complete.Get(low) == Block.BlueWool,
            "a complete portable export preserves out-of-height data omitted from streamed residency");
    }

    static void TestStreamingMetadataValidation(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "bad-stream-metadata");
        var source = World.CreateStreaming(2026);
        source.Set(new(3, 2, 3), Block.RedWool);
        source.ExportSnapshot(path, Position(0, 0), 0, 0);
        string valid = File.ReadAllText(path);
        var target = new World(false);
        Cell sentinel = new(5, 5, 5);
        target.Set(sentinel, Block.Glass);
        bool safe = true;
        foreach (bool duplicate in new[] { false, true })
        {
            var document = JsonNode.Parse(valid)!.AsObject();
            document["KnownColumns"] = duplicate
                ? new JsonArray(new JsonObject { ["X"] = 0, ["Z"] = 0 }, new JsonObject { ["X"] = 0, ["Z"] = 0 })
                : new JsonArray(new JsonObject { ["X"] = int.MaxValue, ["Z"] = 0 });
            File.WriteAllText(path, document.ToJsonString());
            bool rejected = false;
            try { target.Load(path); }
            catch (InvalidDataException) { rejected = true; }
            safe &= rejected && target.Get(sentinel) == Block.Glass && !target.IsStreaming && target.Chunks.Chunks.Count == 1;
        }
        check(safe, "invalid or duplicated v3 streaming columns are rejected before replacing the active world");
    }

    static void TestAutomaticSaveOrientation(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "auto-orientation");
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, path, Options());
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "autosave initial column", .37f, -.21f);
        PumpUntil(world, streamer, Position(4, 0), 0,
            () => Loaded(streamer, 4, 0) && !Loaded(streamer, 0, 0) && File.Exists(path) && !streamer.SaveInProgress,
            "autosave complete after travel", .37f, -.21f);
        var restored = new World(false);
        var state = restored.Load(path);
        check(state.Position == Position(4, 0) && state.Yaw == .37f && state.Pitch == -.21f,
            "automatic unload saves retain the current player camera orientation instead of resetting it to zero");
    }

    static void TestLoadInvalidatesStreamingOwner(Action<bool, string> check, string directory)
    {
        string path = SavePath(directory, "load-owner");
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, path, Options());
        PumpUntil(world, streamer, Position(0, 0), 0, () => Loaded(streamer, 0, 0), "load-owner initial column");
        streamer.FlushSaves(Position(0, 0), 0, 0);
        streamer.Update(Position(10, 0), 0);
        world.Load(path);
        int chunksAfterLoad = world.Chunks.Chunks.Count;
        Thread.Sleep(30);
        bool disposed = false;
        try { streamer.Update(Position(10, 0), 0); }
        catch (ObjectDisposedException) { disposed = true; }
        check(disposed && world.ActiveStreamer is null && streamer.PendingJobs == 0 &&
              chunksAfterLoad == 0 && world.Chunks.Chunks.Count == chunksAfterLoad,
            "explicit world loading disposes the old streaming owner and prevents its pending jobs from publishing late");
    }

}
