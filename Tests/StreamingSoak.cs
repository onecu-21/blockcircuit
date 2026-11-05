using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Two-minute wall-clock travel and persistence soak; no rendering or long-run leak-proof claim.</summary>
static class StreamingSoak
{
    static readonly TimeSpan Duration = TimeSpan.FromSeconds(120);
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(130);
    const int Radius = 1;
    sealed record MemorySample(double Seconds, long ManagedBytes, int ResidentColumns, int ResidentChunks,
        int PendingLoadJobs, int KnownColumns, int StoredChunkReferences, int ActiveFallingBlocks);
    sealed record Receipt(double ElapsedSeconds, long ActualTicks, long ExpectedTicksFromWallTime, double DroppedSeconds,
        int Updates, double UpdateP50Milliseconds, double UpdateP95Milliseconds, double UpdateP99Milliseconds,
        double MaximumUpdateMilliseconds, int MaximumResidentColumns, int MaximumResidentChunks, int MaximumLoadJobs,
        int MaximumIntegrationsPerFrame, int MaximumUnloadsPerFrame, int MaximumSaveCapturesPerFrame,
        int MarkersCreated, int LiveMarkerRevisits, int ReloadMarkersVerified, int KnownColumns,
        int StoredChunkReferences, int JobsAfterDispose, bool SaveBusyAfterDispose, long ManagedHeapAfterFlushGc,
        MemorySample[] MemorySamples);

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-streaming-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var total = Stopwatch.StartNew();
        try
        {
            Receipt receipt = Execute(directory, total, heapBefore);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            long afterDisposal = GC.GetTotalMemory(forceFullCollection: true);
            var report = new
            {
                SchemaVersion = 1, TimestampUtc = DateTime.UtcNow, Success = true,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Scope = "120 seconds of actual wall-clock headless streamed travel with actual elapsed-time 20 TPS accumulation. Includes background generation/save I/O and forced-GC observations, without drawing, GPU frame times, or a leak-proof/indefinite stability claim.",
                RequestedDurationSeconds = Duration.TotalSeconds,
                TotalWorkloadWallSeconds = total.Elapsed.TotalSeconds,
                Limits = new { ResidentColumns = 16, LoadJobs = 2, ResidentChunks = 256, IntegrationsPerFrame = 1, UnloadsPerFrame = 1, SaveCapturesPerFrame = 2 },
                Measurement = receipt,
                TotalAllocatedBytesIncludingWorkers = allocated,
                ManagedHeapBeforeWorkloadGc = heapBefore,
                ManagedHeapAfterAllDisposalsGc = afterDisposal,
                MemoryScope = "Resident chunks are bounded; known-column and saved-file indexes intentionally grow with visited terrain. Non-forcing samples are observations rather than a process working-set measurement."
            };
            File.WriteAllText("streaming-soak-results.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: streaming soak {receipt.ElapsedSeconds:F2}s, {receipt.ActualTicks} actual-time ticks, " +
                $"max {receipt.MaximumResidentColumns}/16 columns and {receipt.MaximumLoadJobs}/2 load jobs; " +
                $"{receipt.ReloadMarkersVerified} distant markers reloaded, jobs after dispose {receipt.JobsAfterDispose}.");
        }
        catch (Exception error)
        {
            File.WriteAllText("streaming-soak-results.json", JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, TimestampUtc = DateTime.UtcNow, Success = false,
                ElapsedSeconds = total.Elapsed.TotalSeconds, Error = error.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(full).StartsWith("blockcircuit-streaming-soak-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected streaming soak cleanup target.");
            Directory.Delete(full, recursive: true);
        }
    }

    static Receipt Execute(string directory, Stopwatch total, long initialHeap)
    {
        string path = Path.Combine(directory, "world.json");
        var options = new StreamingOptions
        {
            MaxResidentColumns = 16, MaxLoadJobs = 2, MaxIntegrationsPerFrame = 1,
            MaxUnloadsPerFrame = 1, MaxSaveCapturesPerFrame = 2, UnloadMargin = 1
        };
        var world = World.CreateStreaming(2026);
        int[] originalInventory = world.Inventory.ToArray();
        using var streamer = new WorldStreamer(world, path, options);
        var clock = new GameClock();
        Action<float> tick = _ => world.Tick();
        var durations = new List<double>(16384);
        var memory = new List<MemorySample>(8) { new(0, initialHeap, 0, 0, 0, 0, 0, 0) };
        var markers = new List<Cell>();
        var marked = new HashSet<Cell>();
        var checkedVisits = new HashSet<int>();
        int maxColumns = 0, maxChunks = 0, maxJobs = 0, maxIntegrations = 0, maxUnloads = 0, maxCaptures = 0;
        int liveRevisits = 0;
        double nextSample = 20;
        Vector3 lastPosition = Position(default);
        Console.WriteLine("Streaming soak started: 120 seconds of wall-clock travel; progress every 20 seconds.");
        var measured = Stopwatch.StartNew();
        long previous = Stopwatch.GetTimestamp();
        while (measured.Elapsed < Duration)
        {
            CheckDeadline(total);
            double seconds = measured.Elapsed.TotalSeconds;
            int visit = (int)(seconds / 2);
            ColumnCoord center = Center(visit);
            lastPosition = Position(center);
            long started = Stopwatch.GetTimestamp();
            streamer.Update(lastPosition, Radius);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            durations.Add(elapsed);
            if (streamer.LastError is not null) throw new IOException("Streaming soak background work failed.", streamer.LastError);
            long now = Stopwatch.GetTimestamp();
            clock.Advance(Stopwatch.GetElapsedTime(previous, now).TotalSeconds, tick);
            previous = now;

            maxColumns = Math.Max(maxColumns, streamer.ResidentColumnCount);
            maxChunks = Math.Max(maxChunks, world.Chunks.Chunks.Count);
            maxJobs = Math.Max(maxJobs, streamer.PendingJobs);
            maxIntegrations = Math.Max(maxIntegrations, streamer.LastIntegrations);
            maxUnloads = Math.Max(maxUnloads, streamer.LastUnloads);
            maxCaptures = Math.Max(maxCaptures, streamer.LastSaveCaptures);
            if (maxColumns > 16 || maxChunks > 256 || maxJobs > 2 || maxIntegrations > 1 || maxUnloads > 1 || maxCaptures > 2)
                throw new InvalidOperationException("Streaming soak exceeded a configured residency or per-frame work budget.");

            var marker = Marker(center);
            if (world.IsCellAvailable(marker))
            {
                if (marked.Contains(marker))
                {
                    if (world.Get(marker) != Block.RedWool) throw new InvalidOperationException("A live reentry lost an earlier marker.");
                    if (checkedVisits.Add(visit)) liveRevisits++;
                }
                else
                {
                    world.Set(marker, Block.RedWool);
                    marked.Add(marker); markers.Add(marker); checkedVisits.Add(visit);
                }
            }
            if (seconds >= nextSample)
            {
                memory.Add(Sample(seconds));
                Console.WriteLine($"Streaming soak {seconds:F0}s: ticks {clock.TotalTicks}, columns {streamer.ResidentColumnCount}/16, " +
                    $"load jobs {streamer.PendingJobs}/2, markers {markers.Count}, heap {memory[^1].ManagedBytes:N0} B.");
                nextSample += 20;
            }
            Thread.Sleep(3);
        }
        clock.Advance(Stopwatch.GetElapsedTime(previous).TotalSeconds, tick);
        measured.Stop();
        memory.Add(Sample(measured.Elapsed.TotalSeconds));
        if (measured.Elapsed < Duration || markers.Count < 3 || !marked.Contains(Marker(default)))
            throw new InvalidOperationException("The soak did not complete its duration and marker coverage.");
        if (!world.Inventory.SequenceEqual(originalInventory))
            throw new InvalidOperationException("Travel or direct marker updates unexpectedly changed inventory.");
        Console.WriteLine($"Streaming soak measured {measured.Elapsed.TotalSeconds:F2}s; flushing and checking saved reentry.");
        CheckDeadline(total);
        streamer.FlushSaves(lastPosition, 0, 0);
        long afterFlush = GC.GetTotalMemory(forceFullCollection: true);
        int knownColumns = world.KnownColumns.Count, references = world.StoredChunkReferences.Count;
        streamer.Dispose();
        int remainingJobs = streamer.PendingJobs;
        bool remainingSave = streamer.SaveInProgress;
        if (remainingJobs != 0 || remainingSave) throw new InvalidOperationException("Primary streamer disposal left background work.");

        var chosen = new[] { Marker(default), markers[1], markers[^1] }.Distinct().ToArray();
        if (chosen.Length != 3) throw new InvalidOperationException("Three distinct saved markers are required.");
        var restored = new World(false);
        CheckDeadline(total);
        restored.Load(path);
        int verified = 0;
        using (var reloaded = new WorldStreamer(restored, path, options))
        {
            foreach (var marker in chosen)
            {
                var center = ColumnCoord.FromChunk(ChunkCoord.FromWorld(marker));
                while (!restored.IsCellAvailable(marker))
                {
                    CheckDeadline(total);
                    reloaded.Update(Position(center), Radius);
                    if (reloaded.LastError is not null) throw new IOException("Soak verification reload failed.", reloaded.LastError);
                    Thread.Sleep(3);
                }
                if (restored.Get(marker) != Block.RedWool || !restored.Inventory.SequenceEqual(originalInventory))
                    throw new InvalidOperationException("A saved marker or exact inventory did not survive reload.");
                verified++;
            }
            reloaded.Dispose();
            remainingJobs += reloaded.PendingJobs;
            remainingSave |= reloaded.SaveInProgress;
        }
        CheckDeadline(total);
        if (remainingJobs != 0 || remainingSave) throw new InvalidOperationException("Reload streamer disposal left background work.");
        durations.Sort();
        return new(measured.Elapsed.TotalSeconds, clock.TotalTicks, (long)Math.Floor(measured.Elapsed.TotalSeconds * PhysicsProfile.TickRate),
            clock.DroppedSeconds, durations.Count, Percentile(.5), Percentile(.95), Percentile(.99), durations[^1],
            maxColumns, maxChunks, maxJobs, maxIntegrations, maxUnloads, maxCaptures,
            markers.Count, liveRevisits, verified, knownColumns, references, remainingJobs, remainingSave, afterFlush, memory.ToArray());

        MemorySample Sample(double seconds) => new(seconds, GC.GetTotalMemory(forceFullCollection: false),
            streamer.ResidentColumnCount, world.Chunks.Chunks.Count, streamer.PendingJobs, world.KnownColumns.Count,
            world.StoredChunkReferences.Count, world.FallingBlocks.Active.Count);
        double Percentile(double fraction) => durations[Math.Clamp((int)Math.Ceiling(durations.Count * fraction) - 1, 0, durations.Count - 1)];
    }

    static void CheckDeadline(Stopwatch watch)
    {
        if (watch.Elapsed > Deadline) throw new TimeoutException("Streaming soak exceeded its 130-second control-loop deadline.");
    }
    static ColumnCoord Center(int visit) => visit % 8 == 0 ? default :
        new((visit % 2 == 0 ? 1 : -1) * visit * 3, visit % 3 == 0 ? -3 : 3);
    static Vector3 Position(ColumnCoord center) => new(center.X * 16 + 8.5f, 55, center.Z * 16 + 8.5f);
    static Cell Marker(ColumnCoord center) => new(center.X * 16 + 2, 50, center.Z * 16 + 2);
}
