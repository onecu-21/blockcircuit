using System.Diagnostics;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Repeatable CPU meshing/allocation measurements; these are not FPS or GPU timings.</summary>
static class EngineBenchmark
{
    sealed record CacheEntry(long Revision, ChunkMeshData Mesh);
    sealed record Measurement(double MedianMilliseconds, long MedianAllocatedBytes, int ChunkCount, int Quads, int Vertices);

    public static void Run()
    {
        Console.WriteLine("BLOCK / CIRCUIT — headless CPU meshing benchmark (not FPS/GPU timings)");
        var terrain = new World();
        ChunkCoord[] coords = terrain.Chunks.Chunks.Keys.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToArray();
        // One warm-up of each algorithm keeps JIT startup out of the recorded samples.
        foreach (ChunkCoord coord in coords) { ChunkMesher.Build(terrain, coord, false); ChunkMesher.Build(terrain, coord); }
        Measurement unmerged = Measure(terrain, coords, greedy: false);
        Measurement greedy = Measure(terrain, coords, greedy: true);
        Console.WriteLine($"Terrain: {coords.Length} sparse chunks; {terrain.Chunks.Chunks.Values.Sum(c => c.NonAirCount):N0} occupied cells");
        Console.WriteLine($"Unit-face mesh: {unmerged.MedianMilliseconds:F3} ms, {unmerged.MedianAllocatedBytes:N0} B allocated, {unmerged.Quads:N0} quads");
        Console.WriteLine($"Greedy mesh:    {greedy.MedianMilliseconds:F3} ms, {greedy.MedianAllocatedBytes:N0} B allocated, {greedy.Quads:N0} quads");

        var cache = new Dictionary<ChunkCoord, CacheEntry>();
        int initialBuilds = Refresh(terrain, cache);
        int staticBuilds = 0;
        var watch = Stopwatch.StartNew();
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        const int staticFrames = 300;
        for (int frame = 0; frame < staticFrames; frame++) staticBuilds += Refresh(terrain, cache);
        long staticAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        watch.Stop();
        double staticMilliseconds = watch.Elapsed.TotalMilliseconds;
        if (staticBuilds != 0) throw new InvalidOperationException("Static cache unexpectedly rebuilt geometry.");

        terrain.Set(new(8, 8, 8), Block.WhiteConcrete);
        int interiorBuilds = Refresh(terrain, cache);
        if (interiorBuilds != 1) throw new InvalidOperationException("An interior edit must rebuild exactly its owner chunk.");
        terrain.Set(new(15, 8, 8), Block.WhiteConcrete);
        int boundaryBuilds = Refresh(terrain, cache);
        if (boundaryBuilds != 2) throw new InvalidOperationException("This populated chunk boundary must rebuild exactly two chunks.");
        Console.WriteLine($"Revision cache simulation: {staticFrames} static frames → {staticBuilds} mesh builds; interior edit → {interiorBuilds}; boundary edit → {boundaryBuilds}");

        const int edits = 2000, batch = 100;
        int stressBuilds = 0, verifiedSnapshots = 0;
        var random = new Random(72031);
        Block[] choices = [Block.Air, Block.Stone, Block.Glass, Block.OakLog, Block.BlueConcrete, Block.Glowstone];
        watch.Restart();
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < edits; i++)
        {
            var cell = new Cell(random.Next(World.Size), random.Next(1, World.Height), random.Next(World.Size));
            terrain.Set(cell, choices[random.Next(choices.Length)]);
            if ((i + 1) % batch != 0) continue;
            stressBuilds += Refresh(terrain, cache);
            foreach (var pair in terrain.Chunks.Chunks)
            {
                ChunkMeshData fresh = ChunkMesher.Build(terrain, pair.Key);
                if (!Equivalent(cache[pair.Key].Mesh, fresh))
                    throw new InvalidOperationException($"Stale cached mesh after edit {i + 1}, chunk {pair.Key}.");
            }
            if (cache.Count != terrain.Chunks.Chunks.Count) throw new InvalidOperationException("Removed chunks remained cached.");
            verifiedSnapshots++;
        }
        long stressAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        watch.Stop();
        double stressMilliseconds = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Stress: {edits:N0} seeded edits, {verifiedSnapshots} full cache/fresh comparisons, {stressBuilds} builds, {stressMilliseconds:F1} ms; no stale geometry");

        var report = new
        {
            SchemaVersion = 1,
            Scope = "CPU only: unmerged vs greedy chunk-mesh generation, managed allocations, and revision-cache simulation. No rendering/FPS/GPU measurements.",
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            TimestampUtc = DateTime.UtcNow,
            SamplesPerAlgorithm = 5,
            Unmerged = unmerged,
            Greedy = greedy,
            QuadReductionPercent = Math.Round(100d * (unmerged.Quads - greedy.Quads) / Math.Max(1, unmerged.Quads), 2),
            Cache = new { InitialBuilds = initialBuilds, StaticFrames = staticFrames, StaticFrameMeshBuilds = staticBuilds,
                StaticMilliseconds = staticMilliseconds, StaticAllocatedBytes = staticAllocatedBytes,
                InteriorEditBuilds = interiorBuilds, BoundaryEditBuilds = boundaryBuilds },
            Stress = new { Seed = 72031, Edits = edits, BatchSize = batch, VerifiedSnapshots = verifiedSnapshots,
                MeshBuilds = stressBuilds, MillisecondsIncludingFreshVerification = stressMilliseconds,
                AllocatedBytesIncludingFreshVerification = stressAllocatedBytes, CachedVsFreshExact = true }
        };
        string path = Path.GetFullPath("benchmark-results.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Report: " + path);
    }

    static Measurement Measure(World world, ChunkCoord[] coords, bool greedy)
    {
        var times = new double[5];
        var allocations = new long[5];
        int quads = 0, vertices = 0;
        for (int sample = 0; sample < times.Length; sample++)
        {
            quads = vertices = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            foreach (ChunkCoord coord in coords)
            {
                ChunkMeshData mesh = ChunkMesher.Build(world, coord, greedy);
                quads += mesh.QuadCount;
                vertices += mesh.Opaque.VertexCount + mesh.Transparent.VertexCount;
            }
            watch.Stop();
            times[sample] = watch.Elapsed.TotalMilliseconds;
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(times); Array.Sort(allocations);
        return new(times[2], allocations[2], coords.Length, quads, vertices);
    }

    static int Refresh(World world, Dictionary<ChunkCoord, CacheEntry> cache)
    {
        // This CPU-only model exercises the same revision protocol without a GL context.
        foreach (ChunkCoord coord in cache.Keys.Where(c => !world.Chunks.Chunks.ContainsKey(c)).ToArray()) cache.Remove(coord);
        int builds = 0;
        foreach (var pair in world.Chunks.Chunks)
        {
            if (cache.TryGetValue(pair.Key, out var old) && old.Revision == pair.Value.Revision) continue;
            cache[pair.Key] = new(pair.Value.Revision, ChunkMesher.Build(world, pair.Key));
            builds++;
        }
        return builds;
    }

    static bool Equivalent(ChunkMeshData a, ChunkMeshData b) => a.ExposedFaceCount == b.ExposedFaceCount &&
        a.Circuits.AsSpan().SequenceEqual(b.Circuits) && Equivalent(a.Opaque, b.Opaque) && Equivalent(a.Transparent, b.Transparent);
    static bool Equivalent(CpuMeshData a, CpuMeshData b) => a.Positions.AsSpan().SequenceEqual(b.Positions) &&
        a.TexCoords.AsSpan().SequenceEqual(b.TexCoords) && a.Colors.AsSpan().SequenceEqual(b.Colors);
}
