using System.Diagnostics;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>CPU-only circuit work comparison. It does not measure rendering or game FPS.</summary>
static class CircuitBenchmark
{
    sealed record Measurement(double MedianMilliseconds, long MedianAllocatedBytes, long TotalVisitedCells, long UpdatedNetworks);

    public static void Run()
    {
        Console.WriteLine("BLOCK / CIRCUIT — incremental circuit CPU benchmark (not FPS/GPU timings)");
        World incremental = BuildFixture(), reference = BuildFixture();
        var target = new Cell(2, 2, 2);
        const int togglesPerSample = 200, samples = 5;
        int networks = incremental.Circuits.NetworkCount;
        if (networks != 60) throw new InvalidOperationException("Benchmark fixture must contain 60 independent networks.");
        for (int i = 0; i < 30; i++)
        {
            incremental.Toggle(target);
            FlipSource(reference, target);
            GameplayTests.ReferencePower(reference, out _);
        }
        Measurement local = Measure(incremental, target, togglesPerSample, samples, incrementalMode: true);
        Measurement full = Measure(reference, target, togglesPerSample, samples, incrementalMode: false);
        Dictionary<Cell, int> expected = GameplayTests.ReferencePower(reference, out _);
        bool correct = GameplayTests.SamePower(incremental.Power, expected) && incremental.Switches.SetEquals(reference.Switches);
        if (!correct) throw new InvalidOperationException("Incremental and full-recompute benchmark results diverged.");

        long updates = incremental.Circuits.TotalNetworkUpdates;
        incremental.Set(new(38, 10, 38), Block.Stone);
        incremental.Circuits.ProcessPending();
        long unrelatedEditUpdates = incremental.Circuits.TotalNetworkUpdates - updates;
        if (unrelatedEditUpdates != 0) throw new InvalidOperationException("Unrelated stone edit recomputed a network.");
        double visitedReduction = 100d * (full.TotalVisitedCells - local.TotalVisitedCells) / Math.Max(1, full.TotalVisitedCells);
        Console.WriteLine($"Fixture: {networks} independent circuits; each has one lever, four wires, and one lamp; all sources initially active");
        Console.WriteLine($"Incremental: {local.MedianMilliseconds:F3} ms / {togglesPerSample} toggles, {local.MedianAllocatedBytes:N0} B per sample, {local.TotalVisitedCells:N0} visited cells total");
        Console.WriteLine($"Full BFS:    {full.MedianMilliseconds:F3} ms / {togglesPerSample} toggles, {full.MedianAllocatedBytes:N0} B per sample, {full.TotalVisitedCells:N0} visited cells total");
        Console.WriteLine($"Visited-cell reduction: {visitedReduction:F2}%; unrelated block edit: {unrelatedEditUpdates} network updates; final power dictionaries match");
        var report = new
        {
            SchemaVersion = 1,
            Scope = "CPU-only independent circuit workload. Incremental timings include World.Toggle edit-history processing; reference timings perform source flipping plus a full independent BFS. Visited counters describe each algorithm's traversed cells; not rendering/FPS/GPU measurements.",
            CounterDefinitions = new { Incremental = "Distinct affected circuit-component cells, including terminal lamps and inactive sources", FullBfs = "Dequeued propagating source/wire cells across all active networks; terminal lamps are not queued" },
            TimestampUtc = DateTime.UtcNow,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            IndependentNetworks = networks,
            BlocksPerNetwork = 6,
            Samples = samples,
            TogglesPerSample = togglesPerSample,
            Incremental = local,
            FullBfs = full,
            VisitedCellReductionPercent = Math.Round(visitedReduction, 2),
            UnrelatedBlockEditNetworkUpdates = unrelatedEditUpdates,
            FinalPowerMatchesReference = correct
        };
        string path = Path.GetFullPath("circuit-benchmark-results.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Report: " + path);
    }

    static World BuildFixture()
    {
        var world = new World(false);
        var levers = new List<Cell>();
        for (int row = 0; row < 12; row++) for (int column = 0; column < 5; column++)
        {
            int x = 2 + column * 7, z = 2 + row * 3;
            for (int offset = 0; offset < 6; offset++)
            {
                world.Set(new(x + offset, 1, z), Block.Stone);
                world.Set(new(x + offset, 2, z), offset == 0 ? Block.Lever : offset == 5 ? Block.Lamp : Block.Wire);
            }
            levers.Add(new(x, 2, z));
        }
        world.Circuits.ProcessPending();
        foreach (Cell lever in levers) world.Toggle(lever);
        world.ClearHistory();
        return world;
    }

    static Measurement Measure(World world, Cell target, int toggles, int samples, bool incrementalMode)
    {
        var times = new double[samples];
        var allocations = new long[samples];
        long visited = 0, networkUpdates = 0;
        for (int sample = 0; sample < samples; sample++)
        {
            long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
            long beforeUpdates = world.Circuits.TotalNetworkUpdates;
            var watch = Stopwatch.StartNew();
            for (int toggle = 0; toggle < toggles; toggle++)
            {
                if (incrementalMode)
                {
                    world.Toggle(target);
                    visited += world.Circuits.LastVisitedCells;
                    if (world.Circuits.LastUpdatedNetworks != 1)
                        throw new InvalidOperationException("A source toggle traversed unrelated circuit networks.");
                }
                else
                {
                    FlipSource(world, target);
                    GameplayTests.ReferencePower(world, out int cells);
                    visited += cells;
                }
            }
            watch.Stop();
            times[sample] = watch.Elapsed.TotalMilliseconds;
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
            networkUpdates += world.Circuits.TotalNetworkUpdates - beforeUpdates;
        }
        Array.Sort(times); Array.Sort(allocations);
        return new(times[samples / 2], allocations[samples / 2], visited, networkUpdates);
    }

    static void FlipSource(World world, Cell target)
    {
        if (!world.Switches.Add(target)) world.Switches.Remove(target);
    }
}
