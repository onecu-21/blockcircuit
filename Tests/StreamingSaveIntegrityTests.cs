using System.Diagnostics;
using System.Numerics;

namespace BlockCircuit;

/// <summary>Cross-frame save snapshots, legacy switch mutation, failure retry, and explicit save coordination.</summary>
static class StreamingSaveIntegrityTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-stream-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestRecaptureBeforePublication(Check, directory);
            TestDirectSwitchMutation(Check, directory);
            TestFailedSaveRetry(Check, directory);
            TestPublicSaveCoordination(Check, directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Streaming save integrity tests passed: {passed}");
    }

    static string PathFor(string directory, string name)
    {
        string child = Path.Combine(directory, name);
        Directory.CreateDirectory(child);
        return Path.Combine(child, "world.json");
    }

    static void Drain(StreamingSaveSession session, int captureBudget = 1)
    {
        var watch = Stopwatch.StartNew();
        while (session.Busy)
        {
            session.Pump(captureBudget);
            if (watch.ElapsedMilliseconds > 15000) throw new InvalidOperationException("Background save did not finish.");
            Thread.Sleep(1);
        }
    }

    static (World World, Vector3 Position, float Yaw, float Pitch) ReadComplete(string path)
    {
        var loaded = new World(false);
        var player = loaded.Load(path);
        if (loaded.IsStreaming) loaded = loaded.CreateCompleteExportWorld();
        loaded.RecomputePower();
        return (loaded, player.Position, player.Yaw, player.Pitch);
    }

    static void TestRecaptureBeforePublication(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        Cell left = new(3, 5, 3), right = new(19, 5, 3);
        world.Set(new(3, 0, 3), Block.Stone);
        world.Set(new(19, 0, 3), Block.Stone);
        if (!world.Place(left, Block.RedWool) || !world.Place(right, Block.BlueWool))
            throw new InvalidOperationException("Snapshot placement fixture failed.");
        string path = PathFor(directory, "recapture");
        using var session = new StreamingSaveSession(world, path);
        session.Request(new(8.5f, 10, 8.5f));
        session.Pump(1);
        check(session.Busy && session.LastCaptures == 1,
            "a one-chunk capture budget leaves a multi-chunk save pending rather than copying every dirty chunk in one frame");

        world.Mine(left); world.Place(left, Block.GreenWool);
        world.Mine(right); world.Place(right, Block.YellowWool);
        world.Tick(); world.Tick();
        Vector3 position = new(24.5f, 10, 8.5f);
        session.UpdatePlayer(position, .41f, -.23f);
        int[] inventory = world.Inventory.ToArray();
        Drain(session);
        if (session.LastError is not null) throw new InvalidOperationException("Snapshot save failed.", session.LastError);
        var saved = ReadComplete(path);
        check(saved.World.Get(left) == Block.GreenWool && saved.World.Get(right) == Block.YellowWool &&
              saved.World.Inventory.AsSpan().SequenceEqual(inventory) && saved.World.TickCount == 2 &&
              saved.Position == position && saved.Yaw == .41f && saved.Pitch == -.23f &&
              world.Chunks.DirtyCoordinates.Count == 0,
            "cross-frame recapture publishes the latest blocks, exact consumed inventory, time, and player metadata together");
    }

    static void TestDirectSwitchMutation(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        Cell left = new(3, 1, 3), right = new(19, 1, 3);
        foreach (var cell in new[] { left, right })
        {
            world.Set(cell + new Cell(0, -1, 0), Block.Stone);
            world.Set(cell, Block.Lever);
        }
        string path = PathFor(directory, "switches");
        world.Save(path, new(8.5f, 10, 8.5f), 0, 0);
        world.Set(new(4, 5, 3), Block.Stone);
        world.Set(new(20, 5, 3), Block.Dirt);
        using var session = new StreamingSaveSession(world, path);
        session.Request(new(8.5f, 10, 8.5f));
        session.Pump(1);
        world.Switches.Add(left); world.Switches.Add(right);
        // Complete the metadata snapshot and launch its worker, then mutate the public
        // legacy set again before the owner thread can acknowledge the receipt.
        session.Pump(int.MaxValue);
        world.Switches.Remove(left); world.Switches.Remove(right);
        Drain(session);
        if (session.LastError is not null) throw new InvalidOperationException("Switch snapshot save failed.", session.LastError);
        var intermediate = ReadComplete(path).World;
        bool retained = intermediate.Switches.Contains(left) && intermediate.Switches.Contains(right) &&
            world.Chunks.DirtyCoordinates.Contains(ChunkCoord.FromWorld(left)) &&
            world.Chunks.DirtyCoordinates.Contains(ChunkCoord.FromWorld(right));
        session.Request(new(8.5f, 10, 8.5f));
        Drain(session);
        var final = ReadComplete(path).World;
        check(retained && !final.Switches.Contains(left) && !final.Switches.Contains(right) &&
              world.Chunks.DirtyCoordinates.Count == 0,
            "direct legacy switch edits are detected during capture and receipt, retaining newer changes for the following save");
    }

    static void TestFailedSaveRetry(Action<bool, string> check, string directory)
    {
        var world = World.CreateStreaming(2026);
        Cell cell = new(3, 5, 3);
        world.Set(cell, Block.Stone);
        string path = PathFor(directory, "failure");
        world.Save(path, new(8.5f, 10, 8.5f), 0, 0);
        byte[] original = File.ReadAllBytes(path);
        world.Set(cell, Block.RedWool);
        using var session = new StreamingSaveSession(world, path);
        bool retained;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            session.Request(new(8.5f, 10, 8.5f));
            Drain(session);
            retained = session.LastError is not null && world.Chunks.Chunks[ChunkCoord.FromWorld(cell)].IsDirty;
        }
        retained &= File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
        session.Request(new(8.5f, 10, 8.5f));
        Drain(session);
        var saved = ReadComplete(path).World;
        check(retained && session.LastError is null && saved.Get(cell) == Block.RedWool &&
              !world.Chunks.Chunks[ChunkCoord.FromWorld(cell)].IsDirty,
            "a failed background save preserves the original manifest and dirty data, then succeeds after the file lock is released");
    }

    static Vector3 Position(int column) => new(column * 16 + 8.5f, 10, 8.5f);

    static void PumpUntil(World world, WorldStreamer streamer, int column, Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            streamer.Update(Position(column), 0);
            world.Tick();
            if (watch.ElapsedMilliseconds > 15000) throw new InvalidOperationException("Save coordination streaming timeout: " + streamer.LastError);
            Thread.Sleep(1);
        }
    }

    static void TestPublicSaveCoordination(Action<bool, string> check, string directory)
    {
        string originalPath = PathFor(directory, "owner-original");
        string copyPath = PathFor(directory, "owner-copy");
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, originalPath, new StreamingOptions { MaxLoadJobs = 2, MaxResidentColumns = 16 });
        PumpUntil(world, streamer, 0, () => streamer.LoadedColumns.Contains(new(0, 0)));
        Cell first = new(3, 30, 3), second = new(67, 30, 3), third = new(131, 30, 3);
        world.Set(first, Block.RedWool);
        PumpUntil(world, streamer, 4, () => streamer.LoadedColumns.Contains(new(4, 0)) && !streamer.LoadedColumns.Contains(new(0, 0)));
        world.Set(second, Block.BlueWool);
        PumpUntil(world, streamer, 8, () => streamer.LoadedColumns.Contains(new(8, 0)) && !streamer.LoadedColumns.Contains(new(4, 0)));
        world.Set(third, Block.GreenWool);
        streamer.Update(Position(12), 0);
        if (!streamer.SaveInProgress) throw new InvalidOperationException("Save coordination fixture did not start an automatic save.");
        world.Save(copyPath, Position(8), 1.1f, -.4f);
        string? copiedGeneration = world.StoredGeneration;
        byte[] copiedManifest = File.ReadAllBytes(copyPath);
        var copy = ReadComplete(copyPath);
        check(copy.World.Get(first) == Block.RedWool && copy.World.Get(second) == Block.BlueWool &&
              copy.World.Get(third) == Block.GreenWool && copy.Position == Position(8) && copy.Yaw == 1.1f && copy.Pitch == -.4f,
            "public save-as coordinates with a busy streamer and copies both unloaded references and the newest resident edits");

        for (int frame = 0; frame < 20; frame++)
        {
            streamer.Update(Position(8), 0);
            Thread.Sleep(1);
        }
        check(!streamer.SaveInProgress && world.StoredPath == Path.GetFullPath(copyPath) &&
              world.StoredGeneration == copiedGeneration && File.ReadAllBytes(copyPath).AsSpan().SequenceEqual(copiedManifest),
            "drained background receipts cannot overwrite the completed public save-as generation or its manifest");
    }
}
