using System.Diagnostics;

namespace BlockCircuit;

static class ChunkMeshSchedulerTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            count++;
        }
        TestSnapshots(Check);
        TestBoundedJobs(Check);
        TestStaleJobs(Check);
        TestResetAndFailure(Check);
        Console.WriteLine($"Chunk mesh scheduler tests passed: {count}");
    }

    static World Fixture()
    {
        var world = new World(false);
        world.Set(new(3, 2, 3), Block.Stone);
        world.Set(new(4, 2, 3), Block.Glass);
        world.Set(new(5, 2, 3), Block.Lamp);
        world.Set(new(6, 2, 3), Block.Wire);
        world.Set(new(7, 2, 3), Block.Lever);
        world.Power[new(5, 2, 3)] = 15;
        world.Set(new(19, 2, 3), Block.BlueWool);
        world.Set(new(35, 2, 3), Block.RedWool);
        return world;
    }

    static bool Same(ChunkMeshData first, ChunkMeshData second) =>
        first.ExposedFaceCount == second.ExposedFaceCount && first.Circuits.SequenceEqual(second.Circuits) &&
        Same(first.Opaque, second.Opaque) && Same(first.Transparent, second.Transparent);
    static bool Same(CpuMeshData first, CpuMeshData second) => first.Positions.SequenceEqual(second.Positions) &&
        first.TexCoords.SequenceEqual(second.TexCoords) && first.Colors.SequenceEqual(second.Colors);

    static ChunkMeshResult WaitResult(ChunkMeshScheduler scheduler, World world)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (scheduler.TryTakeCompleted(world, out var result)) return result;
            Thread.Sleep(1);
        }
        throw new TimeoutException("CPU mesh job did not complete within its test deadline.");
    }
    static void Drain(ChunkMeshScheduler scheduler, World world)
    {
        var timeout = Stopwatch.StartNew();
        while (scheduler.PendingCount != 0 && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            scheduler.CancelObsolete(world);
            Thread.Sleep(1);
        }
        if (scheduler.PendingCount != 0) throw new TimeoutException("Cancelled mesh jobs were not observed.");
    }

    static void TestSnapshots(Action<bool, string> check)
    {
        var world = Fixture();
        var coordinate = new ChunkCoord(0, 0, 0);
        var snapshot = ChunkMesher.Capture(world, coordinate);
        var expected = ChunkMesher.Build(world, coordinate);
        check(Same(expected, ChunkMesher.Build(snapshot)), "snapshot CPU meshing matches the established world mesher attributes and circuit models");
        check(Same(ChunkMesher.Build(world, coordinate, false), ChunkMesher.Build(snapshot, false)),
            "snapshot meshing preserves the unmerged reference path as well as greedy geometry");
        world.Set(new(3, 2, 3), Block.Air);
        world.Set(new(4, 2, 3), Block.Stone);
        world.Power[new(5, 2, 3)] = 0;
        world.Set(new(6, 2, 3), Block.Air);
        var background = Task.Run(() => ChunkMesher.Build(snapshot)).GetAwaiter().GetResult();
        check(Same(expected, background) && !Same(background, ChunkMesher.Build(world, coordinate)),
            "captured blocks, lamp power, and circuit positions remain isolated from later live-world edits");
        background.Circuits[0] = new(1000, 1000, 1000);
        if (background.Opaque.Positions.Length != 0) background.Opaque.Positions[0] = float.NaN;
        check(Same(expected, ChunkMesher.Build(snapshot)), "mutating one mesh result cannot mutate the private snapshot or a subsequent worker result");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool rejected = false;
        try { ChunkMesher.Build(snapshot, cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "cancelled snapshot builds stop before producing mesh output");
    }

    static void TestBoundedJobs(Action<bool, string> check)
    {
        var world = Fixture();
        var a = new ChunkCoord(0, 0, 0); var b = new ChunkCoord(1, 0, 0); var c = new ChunkCoord(2, 0, 0);
        using var scheduler = new ChunkMeshScheduler(2);
        check(scheduler.Request(world, a, world.Chunks.Chunks[a]) &&
              scheduler.Request(world, b, world.Chunks.Chunks[b]) &&
              !scheduler.Request(world, c, world.Chunks.Chunks[c]) && scheduler.PendingCount == 2 && scheduler.MaximumJobs == 2,
            "CPU scheduling is bounded even when completed results have not yet been uploaded");
        var first = WaitResult(scheduler, world);
        check(first.SourceChunk.Lifecycle.State == ChunkState.Meshing &&
              first.SourceChunk.Lifecycle.CompleteMeshing(first.OperationToken),
            "taking CPU output leaves lifecycle completion to the renderer upload owner");
        var second = WaitResult(scheduler, world);
        second.SourceChunk.Lifecycle.CompleteMeshing(second.OperationToken);
        check(scheduler.PendingCount == 0 && scheduler.TotalScheduled == 2 && scheduler.TotalCompleted == 2 &&
              first.WorkerThreadId != Environment.CurrentManagedThreadId && second.WorkerThreadId != Environment.CurrentManagedThreadId,
            "CPU jobs run off the owner thread and all accepted completions leave the bounded queue");
        check(Same(first.Data, ChunkMesher.Build(world, first.Coord)) &&
              Same(second.Data, ChunkMesher.Build(world, second.Coord)) && scheduler.LastError is null,
            "background output matches current synchronous geometry without accessing a graphics context");
        check(scheduler.Request(world, a, world.Chunks.Chunks[a]), "a Ready chunk can be scheduled for a fresh renderer upload");
        var again = WaitResult(scheduler, world);
        again.SourceChunk.Lifecycle.CompleteMeshing(again.OperationToken);
        bool offThreadRejected = Task.Run(() =>
        {
            try { scheduler.Reset(); return false; }
            catch (InvalidOperationException) { return true; }
        }).GetAwaiter().GetResult();
        check(offThreadRejected, "worker threads cannot mutate the scheduler or live chunk lifecycle controls");
    }

    static void TestStaleJobs(Action<bool, string> check)
    {
        var world = Fixture();
        var coordinate = new ChunkCoord(0, 0, 0);
        using var scheduler = new ChunkMeshScheduler(1);
        Chunk old = world.Chunks.Chunks[coordinate];
        scheduler.Request(world, coordinate, old);
        world.Set(new(3, 2, 3), Block.Dirt);
        scheduler.CancelObsolete(world);
        Drain(scheduler, world);
        check(!scheduler.TryTakeCompleted(world, out _) && old.Lifecycle.State == ChunkState.Generated && scheduler.TotalDiscarded == 1,
            "a block edit cancels an obsolete mesh and prevents stale CPU geometry from publishing");
        check(scheduler.Request(world, coordinate, old), "a chunk invalidated during CPU work can retry from its new revision");
        var fresh = WaitResult(scheduler, world);
        check(fresh.Revision == old.Revision && Same(fresh.Data, ChunkMesher.Build(world, coordinate)),
            "retry results carry the current source revision and current geometry");
        fresh.SourceChunk.Lifecycle.CompleteMeshing(fresh.OperationToken);
        world.Set(new(15, 2, 3), Block.Stone);
        scheduler.Request(world, coordinate, old);
        long revision = old.Revision;
        world.Set(new(16, 2, 3), Block.Stone);
        scheduler.CancelObsolete(world); Drain(scheduler, world);
        check(old.Revision != revision && !scheduler.TryTakeCompleted(world, out _),
            "neighbor boundary changes invalidate a captured halo before its mesh can be uploaded");
        scheduler.Request(world, coordinate, old);
        world.Chunks.Remove(coordinate);
        scheduler.CancelObsolete(world); Drain(scheduler, world);
        check(!scheduler.TryTakeCompleted(world, out _) && old.Lifecycle.State == ChunkState.Unloaded,
            "an unloaded chunk cannot receive the output of its previous worker");
        world.Set(new(3, 2, 3), Block.Glowstone);
        Chunk replacement = world.Chunks.Chunks[coordinate];
        check(!ReferenceEquals(old, replacement) && scheduler.Request(world, coordinate, replacement),
            "reentering the same coordinate schedules its replacement chunk rather than the retired incarnation");
        var result = WaitResult(scheduler, world);
        check(ReferenceEquals(result.SourceChunk, replacement) && Same(result.Data, ChunkMesher.Build(world, coordinate)),
            "completion source identity prevents old-incarnation geometry from replacing a recreated chunk");
        result.SourceChunk.Lifecycle.CompleteMeshing(result.OperationToken);
    }

    static void TestResetAndFailure(Action<bool, string> check)
    {
        var world = Fixture(); var replacementWorld = Fixture();
        var coordinate = new ChunkCoord(0, 0, 0);
        using var scheduler = new ChunkMeshScheduler(1);
        scheduler.Request(world, coordinate, world.Chunks.Chunks[coordinate]);
        scheduler.Reset();
        check(scheduler.PendingCount <= scheduler.MaximumJobs, "reset cancellation retains running CPU jobs in the concurrency budget until observed");
        Drain(scheduler, world);
        check(!scheduler.TryTakeCompleted(replacementWorld, out _) &&
              scheduler.Request(replacementWorld, coordinate, replacementWorld.Chunks.Chunks[coordinate]),
            "world replacement discards prior-world CPU output and permits new-world work after cancellation");
        var result = WaitResult(scheduler, replacementWorld);
        check(ReferenceEquals(result.SourceChunk, replacementWorld.Chunks.Chunks[coordinate]),
            "a reset completion can only reference the active world's current chunk");
        result.SourceChunk.Lifecycle.CompleteMeshing(result.OperationToken);

        // Deliberately bypass the validated manager in this test to exercise a faulted worker.
        var broken = replacementWorld.Chunks.Chunks[coordinate];
        broken.Set(new(8, 2, 3), (Block)int.MaxValue);
        scheduler.Request(replacementWorld, coordinate, broken);
        var timeout = Stopwatch.StartNew();
        while (scheduler.PendingCount != 0 && timeout.Elapsed < TimeSpan.FromSeconds(10))
        { scheduler.TryTakeCompleted(replacementWorld, out _); Thread.Sleep(1); }
        check(scheduler.PendingCount == 0 && scheduler.LastError is ArgumentOutOfRangeException &&
              broken.Lifecycle.State == ChunkState.Generated,
            "faulted workers are observed, reported, and return their chunk to a retryable state");
        replacementWorld.Set(new(8, 2, 3), Block.Stone);
        scheduler.Reset();
        check(scheduler.LastError is null && scheduler.Request(replacementWorld, coordinate, broken),
            "reset clears an observed worker error and permits a corrected chunk to retry");
        scheduler.Dispose();
        check(scheduler.PendingCount == 0 && broken.Lifecycle.State == ChunkState.Generated,
            "dispose cancels and joins pending CPU workers without leaving Meshing lifecycles or unobserved jobs");
        bool disposedRejected = false;
        try { scheduler.Request(replacementWorld, coordinate, broken); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        check(disposedRejected, "a disposed mesh scheduler cannot start replacement work");
    }
}
