using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Headless CPU tick measurement for a bounded falling-block workload.</summary>
static class FallingBenchmark
{
    const int BlockCount = 512;
    const int TickLimit = 1000;
    public static void Run()
    {
        // Warm the detach, sweep, settlement and notification paths separately.
        var warmup = Fixture(16, 8);
        using (warmup.Events.Subscribe<BlockChangedEvent>(_ => { }))
            for (int tick = 0; tick < 100 && (warmup.FallingBlocks.PendingCount != 0 || warmup.FallingBlocks.Active.Count != 0); tick++)
                warmup.Tick();

        var world = Fixture(BlockCount, 48);
        int[] initialInventory = world.Inventory.ToArray();
        var times = new double[TickLimit];
        int staticBlocks = BlockCount;
        bool conserved = true;
        using var listener = world.Events.Subscribe<BlockChangedEvent>(change =>
        {
            if (change.Previous == Block.Sand) staticBlocks--;
            if (change.Current == Block.Sand) staticBlocks++;
            conserved &= staticBlocks + world.FallingBlocks.Active.Count == BlockCount;
        });
        int ticks = 0, maximumActive = 0, maximumScheduledWork = 0, maximumActiveWork = 0;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        while (world.FallingBlocks.PendingCount != 0 || world.FallingBlocks.Active.Count != 0)
        {
            if (ticks == TickLimit) throw new InvalidOperationException("Falling benchmark exceeded its settlement tick bound.");
            long tickStart = Stopwatch.GetTimestamp();
            world.Tick();
            times[ticks++] = Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
            maximumActive = Math.Max(maximumActive, world.FallingBlocks.Active.Count);
            maximumScheduledWork = Math.Max(maximumScheduledWork, world.FallingBlocks.LastScheduledChecks);
            maximumActiveWork = Math.Max(maximumActiveWork, world.FallingBlocks.LastActiveUpdates);
        }
        double totalMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        int settled = world.OccupiedCells().Count(cell => world.Get(cell) == Block.Sand);
        bool inventoryUnchanged = world.Inventory.SequenceEqual(initialInventory);
        if (settled != BlockCount || staticBlocks != BlockCount || !conserved || !inventoryUnchanged ||
            maximumScheduledWork > 512 || maximumActiveWork > 256)
            throw new InvalidOperationException("Falling benchmark conservation or work-budget invariant failed.");

        var sorted = times.Take(ticks).Order().ToArray();
        double Percentile(double value) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * value) - 1)];
        var report = new
        {
            SchemaVersion = 1,
            TimestampUtc = DateTime.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Scope = "Headless synchronous World.Tick CPU wall time for 512 sand blocks in separate X/Z cells at Y48 over a Y0 floor. Includes constant-time event conservation instrumentation. No render, GPU, I/O or streaming performance measurement.",
            BlockCount,
            TicksUntilSettled = ticks,
            SettlementTickLimit = TickLimit,
            TotalMilliseconds = totalMilliseconds,
            ManagedAllocatedBytes = allocated,
            P50TickMilliseconds = Percentile(.50),
            P95TickMilliseconds = Percentile(.95),
            P99TickMilliseconds = Percentile(.99),
            MaximumTickMilliseconds = sorted[^1],
            MaximumActiveBlocks = maximumActive,
            ConfiguredScheduledChecksPerTick = world.FallingBlocks.MaxScheduledChecksPerTick,
            ConfiguredActiveUpdatesPerTick = world.FallingBlocks.MaxActiveUpdatesPerTick,
            MaximumObservedScheduledChecks = maximumScheduledWork,
            MaximumObservedActiveUpdates = maximumActiveWork,
            LastScheduledChecks = world.FallingBlocks.LastScheduledChecks,
            LastActiveUpdates = world.FallingBlocks.LastActiveUpdates,
            SettledBlocks = settled,
            ConservedDuringEveryNotification = conserved,
            InventoryUnchanged = inventoryUnchanged,
            AllSettledWithinLimit = true
        };
        File.WriteAllText("falling-benchmark-results.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Falling CPU: {BlockCount} conserved blocks settled in {ticks} ticks; P50/P95/P99 {report.P50TickMilliseconds:F3}/{report.P95TickMilliseconds:F3}/{report.P99TickMilliseconds:F3} ms; {allocated:N0} B managed allocation.");
    }

    static World Fixture(int count, int height)
    {
        var world = new World(false);
        for (int i = 0; i < count; i++)
        {
            int x = i % 32, z = i / 32;
            world.Set(new(x, 0, z), Block.Stone);
            world.Set(new(x, height, z), Block.Sand);
        }
        return world;
    }
}
