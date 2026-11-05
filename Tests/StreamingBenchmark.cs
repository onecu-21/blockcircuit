using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Bounded headless travel, residency and background persistence measurements, without GPU or real FPS claims.</summary>
static class StreamingBenchmark
{
    const int Radius = 1;
    const int MeasuredStops = 12;
    const int MinimumFramesPerStop = 12;
    sealed record Workload(
        int MeasuredFrames, long SimulatedTicks, double TravelWallMilliseconds, double FinalFlushMilliseconds,
        double UpdateP50Milliseconds, double UpdateP95Milliseconds, double UpdateP99Milliseconds,
        double MaximumUpdateMilliseconds, double MaximumReportedUpdateMilliseconds,
        int MaximumResidentColumns, int MaximumResidentChunks, int MaximumPendingLoadJobs,
        int MaximumIntegrationsPerFrame, int MaximumUnloadsPerFrame, int MaximumSaveCapturesPerFrame,
        int ResidentColumnsAfterTravel, int ResidentChunksAfterTravel,
        int GenerationJobsStarted, int CancelledJobs, int UnloadedColumns,
        long TravelAndFlushAllocatedBytesIncludingWorkers, long ManagedHeapAfterWarmupGc,
        long ManagedHeapAfterTravelGc, bool PastEditWasUnloaded, bool PastEditRestoredAfterReload);

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-streaming-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var totalWatch = Stopwatch.StartNew();
        try
        {
            Workload workload = Travel(directory);
            totalWatch.Stop();
            long totalAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            long heapAfterDispose = GC.GetTotalMemory(forceFullCollection: true);
            var report = new
            {
                SchemaVersion = 1,
                TimestampUtc = DateTime.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Scope = "Headless bounded travel through twelve far-apart 3x3 column neighborhoods after two warmup stops. Owner-thread Update timing under background generation/save I/O. Simulated 60 Hz frames and 20 TPS; no drawing, actual FPS, GPU timing, or long-run stability claim.",
                DriverFramesPerSecond = 60,
                SimulationTicksPerSecond = PhysicsProfile.TickRate,
                WarmupStops = 2,
                MeasuredStops,
                RadiusChunks = Radius,
                Limits = new { ResidentColumns = 16, LoadJobs = 2, IntegrationsPerFrame = 1, UnloadsPerFrame = 1, SaveCapturesPerFrame = 2 },
                Workload = workload,
                CounterScope = "GenerationJobsStarted, CancelledJobs and UnloadedColumns include warmup. Frame maxima, percentiles and SimulatedTicks cover measured travel; the travel allocation interval also includes the explicit final flush.",
                TotalWorkloadWallMilliseconds = totalWatch.Elapsed.TotalMilliseconds,
                TotalWorkloadAllocatedBytesIncludingWorkers = totalAllocated,
                ManagedHeapBeforeWorkloadGc = heapBefore,
                ManagedHeapAfterDisposalGc = heapAfterDispose,
                HeapScope = "Forced-GC observations include CLR caches and measured telemetry; retained metadata grows with visited columns. This is not a process-residency or leak-proof measurement.",
                BudgetChecksPassed = true
            };
            File.WriteAllText("streaming-benchmark-results.json", JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Streaming headless: {workload.MeasuredFrames} frames / {MeasuredStops} stops; " +
                $"Update P95/P99 {workload.UpdateP95Milliseconds:F3}/{workload.UpdateP99Milliseconds:F3} ms; " +
                $"max {workload.MaximumResidentColumns}/16 columns, {workload.MaximumPendingLoadJobs}/2 load jobs; unloaded edit restored.");
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(full).StartsWith("blockcircuit-streaming-benchmark-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected streaming benchmark cleanup target.");
            Directory.Delete(full, recursive: true);
        }
    }

    static Workload Travel(string directory)
    {
        string path = Path.Combine(directory, "world.json");
        var options = new StreamingOptions
        {
            MaxLoadJobs = 2,
            MaxIntegrationsPerFrame = 1,
            MaxUnloadsPerFrame = 1,
            MaxSaveCapturesPerFrame = 2,
            UnloadMargin = 1,
            MaxResidentColumns = 16
        };
        var world = World.CreateStreaming(2026);
        using var streamer = new WorldStreamer(world, path, options);
        var clock = new GameClock();
        var deadline = Stopwatch.StartNew();
        var durations = new List<double>(512);
        bool measuring = false;
        int frames = 0, maxColumns = 0, maxChunks = 0, maxJobs = 0;
        int maxIntegrations = 0, maxUnloads = 0, maxCaptures = 0;
        double maxReported = 0;

        PumpTo(world, streamer, new(0, 0));
        PumpTo(world, streamer, new(6, -3));
        Vector3 lastPosition = Position(new(6, -3));
        streamer.FlushSaves(lastPosition, 0, 0);
        long heapAfterWarmup = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBeforeTravel = GC.GetTotalAllocatedBytes(precise: true);
        long tickBeforeTravel = clock.TotalTicks;
        measuring = true;
        var travelWatch = Stopwatch.StartNew();
        Cell marker = default;
        int markerStock = world.Inventory[(int)Block.BlueWool];
        for (int stop = 0; stop < MeasuredStops; stop++)
        {
            var column = new ColumnCoord(12 + stop * 6, stop % 2 == 0 ? 4 : -4);
            PumpTo(world, streamer, column);
            lastPosition = Position(column);
            if (stop == 0)
            {
                marker = new(column.X * 16 + 2, 60, column.Z * 16 + 2);
                if (!world.Place(marker, Block.BlueWool))
                    throw new InvalidOperationException("The streaming benchmark marker could not be placed.");
            }
        }
        travelWatch.Stop();
        long measuredTicks = clock.TotalTicks - tickBeforeTravel;
        measuring = false;
        if (durations.Count < 100 || maxColumns > options.MaxResidentColumns || maxJobs > options.MaxLoadJobs ||
            maxIntegrations > options.MaxIntegrationsPerFrame || maxUnloads > options.MaxUnloadsPerFrame ||
            maxCaptures > options.MaxSaveCapturesPerFrame || clock.DroppedSeconds != 0)
            throw new InvalidOperationException("Streaming benchmark exceeded its frame/job/residency budgets.");

        bool markerEvicted = !world.Chunks.Chunks.ContainsKey(ChunkCoord.FromWorld(marker)) && !world.IsCellAvailable(marker);
        if (!markerEvicted) throw new InvalidOperationException("The distant edited column was not unloaded.");
        int columnsAfterTravel = streamer.ResidentColumnCount, chunksAfterTravel = world.Chunks.Chunks.Count;
        var flushWatch = Stopwatch.StartNew();
        streamer.FlushSaves(lastPosition, 0, 0);
        flushWatch.Stop();
        long allocatedTravel = GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeTravel;
        long heapAfterTravel = GC.GetTotalMemory(forceFullCollection: true);

        var restored = new World(false);
        restored.Load(path);
        using (var reloaded = new WorldStreamer(restored, path, options))
        {
            PumpTo(restored, reloaded, ColumnCoord.FromChunk(ChunkCoord.FromWorld(marker)));
            if (restored.Get(marker) != Block.BlueWool || restored.Inventory[(int)Block.BlueWool] != markerStock - 1)
                throw new InvalidOperationException("A distant edit or its exact quantity was lost after flush and reload.");
        }
        double[] sorted = durations.ToArray();
        Array.Sort(sorted);
        return new(frames, measuredTicks, travelWatch.Elapsed.TotalMilliseconds,
            flushWatch.Elapsed.TotalMilliseconds, Percentile(.5), Percentile(.95), Percentile(.99), sorted[^1], maxReported,
            maxColumns, maxChunks, maxJobs, maxIntegrations, maxUnloads, maxCaptures,
            columnsAfterTravel, chunksAfterTravel, streamer.GenerationJobsStarted, streamer.CancelledJobs, streamer.UnloadedColumns,
            allocatedTravel, heapAfterWarmup, heapAfterTravel, markerEvicted, true);

        double Percentile(double fraction) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

        void PumpTo(World target, WorldStreamer controller, ColumnCoord center)
        {
            int stopFrames = 0;
            do
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Streaming benchmark travel exceeded its 30-second control-loop deadline.");
                long started = Stopwatch.GetTimestamp();
                controller.Update(Position(center), Radius);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (controller.LastError is not null) throw new IOException("Streaming benchmark background work failed.", controller.LastError);
                clock.Advance(1d / 60, _ => target.Tick());
                if (measuring)
                {
                    durations.Add(milliseconds); frames++;
                    maxColumns = Math.Max(maxColumns, controller.ResidentColumnCount);
                    maxChunks = Math.Max(maxChunks, target.Chunks.Chunks.Count);
                    maxJobs = Math.Max(maxJobs, controller.PendingJobs);
                    maxIntegrations = Math.Max(maxIntegrations, controller.LastIntegrations);
                    maxUnloads = Math.Max(maxUnloads, controller.LastUnloads);
                    maxCaptures = Math.Max(maxCaptures, controller.LastSaveCaptures);
                    maxReported = Math.Max(maxReported, controller.LastUpdateMilliseconds);
                }
                stopFrames++;
                Thread.Sleep(1);
            }
            while (stopFrames < MinimumFramesPerStop || controller.PendingJobs != 0 || controller.SaveInProgress ||
                controller.ResidentColumnCount > (Radius * 2 + 1) * (Radius * 2 + 1) || !NeighborhoodAvailable(target, center));
        }
    }

    static Vector3 Position(ColumnCoord center) => new(center.X * 16 + 8.5f, 55, center.Z * 16 + 8.5f);
    static bool NeighborhoodAvailable(World world, ColumnCoord center)
    {
        for (int z = -Radius; z <= Radius; z++) for (int x = -Radius; x <= Radius; x++)
            if (!world.IsCellAvailable(new((center.X + x) * 16 + 8, 60, (center.Z + z) * 16 + 8))) return false;
        return true;
    }
}
