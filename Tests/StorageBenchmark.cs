using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace BlockCircuit;

/// <summary>Small signed-coordinate file-store workload; includes hashing, JSON and synchronous disk flush.</summary>
static class StorageBenchmark
{
    sealed record Measurement(double MedianMilliseconds, long MedianAllocatedBytes, int WrittenChunks, int ReusedChunks);
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-storage-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var world = new World(false);
            for (int i = 0; i < 32; i++) world.Set(new((i - 16) * 16 + 1, 2, 1), Block.Stone);
            world.Save(Path.Combine(directory, "warmup.json"), world.Spawn(), 0, 0);
            int next = 0;
            Measurement full = Measure(() => world.Save(Path.Combine(directory, $"full-{next++}.json"), world.Spawn(), 0, 0), world);
            string path = Path.Combine(directory, "world.json");
            world.Save(path, world.Spawn(), 0, 0);
            Measurement unchanged = Measure(() => world.Save(path, world.Spawn(), 0, 0), world);
            bool flip = false;
            Measurement dirty = Measure(() =>
            {
                world.Set(new(-15, 2, 1), (flip = !flip) ? Block.Dirt : Block.Stone);
                world.Save(path, world.Spawn(), 0, 0);
            }, world);
            var loaded = new World(false);
            var watch = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            loaded.Load(path);
            long loadAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            if (loaded.Chunks.Chunks.Count != 32 || world.OccupiedCells().Any(cell => loaded.Get(cell) != world.Get(cell)) ||
                full.WrittenChunks != 32 || unchanged.WrittenChunks != 0 || unchanged.ReusedChunks != 32 ||
                dirty.WrittenChunks != 1 || dirty.ReusedChunks != 31)
                throw new InvalidOperationException("Storage benchmark content or write budget mismatch.");
            var report = new
            {
                SchemaVersion = 1, TimestampUtc = DateTime.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Scope = "32 sparse signed-coordinate chunks, five samples; synchronous JSON, checksum verification and file flush. Not FPS or streaming performance.",
                Samples = 5, ChunkCount = 32, FullSave = full, UnchangedSave = unchanged, OneDirtySave = dirty,
                LoadMilliseconds = watch.Elapsed.TotalMilliseconds, LoadAllocatedBytes = loadAllocated,
                ExactContentRoundtrip = true
            };
            File.WriteAllText("storage-benchmark-results.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Storage CPU/I-O median: full {full.MedianMilliseconds:F3} ms/32 writes; unchanged {unchanged.MedianMilliseconds:F3} ms/0 writes; dirty {dirty.MedianMilliseconds:F3} ms/1 write; exact roundtrip.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    static Measurement Measure(Action operation, World world)
    {
        var times = new double[5];
        var allocations = new long[5];
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            operation();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(times); Array.Sort(allocations);
        return new(times[2], allocations[2], world.LastSaveReport.WrittenChunks, world.LastSaveReport.ReusedChunks);
    }
}
