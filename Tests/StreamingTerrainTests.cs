namespace BlockCircuit;

/// <summary>Determinism, signed coordinates, cross-chunk terrain and vegetation contracts.</summary>
static class StreamingTerrainTests
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
        Check(StreamingTerrainGenerator.Version == 2 && StreamingTerrainGenerator.MinimumY == 0 &&
              StreamingTerrainGenerator.MaximumY == 63, "streaming terrain has explicit v2 provenance and a 64-block generation height");
        var coordinate = new ChunkCoord(-2, 0, 3);
        var first = StreamingTerrainGenerator.Generate(2026, coordinate);
        var repeat = StreamingTerrainGenerator.Generate(2026, coordinate);
        Check(first.Length == 4096 && first.SequenceEqual(repeat), "the same signed chunk and seed produce identical independent 16-cubed snapshots");
        repeat[0] = Block.Air;
        Check(first[0] == Block.Bedrock, "workers return independently owned arrays rather than shared mutable terrain buffers");
        Check(!first.SequenceEqual(StreamingTerrainGenerator.Generate(2027, coordinate)), "changing the seed changes a chunk's terrain and materials");
        Check(first.All(block => (uint)block < 180), "generated terrain uses only registered built-in block identities");
        bool bedrock = true;
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
            bedrock &= first[z * 16 + x] == Block.Bedrock;
        Check(bedrock, "bedrock remains continuous at y zero in negative-coordinate chunks");
        Check(StreamingTerrainGenerator.Generate(2026, new(-2, -1, 3)).All(block => block == Block.Air) &&
              StreamingTerrainGenerator.Generate(2026, new(-2, 4, 3)).All(block => block == Block.Air),
            "chunks outside the generation height remain empty without wrapping vertical coordinates");

        bool clearing = true;
        for (int x = 16; x <= 26; x++) for (int z = 16; z <= 26; z++)
            clearing &= StreamingTerrainGenerator.SurfaceHeight(2026, x, z) == 9 &&
                StreamingTerrainGenerator.GetBlock(2026, new(x, 9, z)) == Block.Grass &&
                StreamingTerrainGenerator.GetBlock(2026, new(x, 10, z)) == Block.Air &&
                StreamingTerrainGenerator.GetBlock(2026, new(x, 11, z)) == Block.Air;
        Check(clearing, "the spawn clearing stays flat at height nine with two air blocks of headroom");

        ChunkCoord[] coordinates = [new(-2, 0, -1), new(-1, 0, -1), new(-2, 1, -1), new(-1, 1, -1),
            new(0, 0, 0), new(0, 1, 0), new(1, 0, 0), new(1, 1, 0)];
        var forward = coordinates.ToDictionary(c => c, c => StreamingTerrainGenerator.Generate(731, c));
        var reverse = coordinates.Reverse().ToDictionary(c => c, c => StreamingTerrainGenerator.Generate(731, c));
        Check(coordinates.All(c => forward[c].SequenceEqual(reverse[c])),
            "adjacent horizontal and vertical chunks are independent of generation order");
        var parallel = Task.WhenAll(coordinates.Select(c => Task.Run(() => (Coord: c, Blocks: StreamingTerrainGenerator.Generate(731, c)))))
            .GetAwaiter().GetResult();
        Check(parallel.All(result => result.Blocks.SequenceEqual(forward[result.Coord])),
            "parallel background generation matches sequential chunks without shared RNG or field state");

        bool seams = true;
        foreach (var coord in coordinates)
        {
            var origin = coord.Origin;
            var blocks = forward[coord];
            for (int edge = 0; edge < 16; edge++)
            foreach (int y in new[] { 0, 1, 5, 10, 15 })
            {
                Cell[] local = [new(0, y, edge), new(15, y, edge), new(edge, y, 0), new(edge, y, 15)];
                foreach (var cell in local)
                    seams &= blocks[(cell.Y * 16 + cell.Z) * 16 + cell.X] ==
                        StreamingTerrainGenerator.GetBlock(731, origin + cell);
            }
        }
        Check(seams, "all sampled sides of adjacent chunks match an independently addressed global terrain field");

        bool vertical = true;
        for (int chunkY = 0; chunkY < 4; chunkY++)
        {
            var coord = new ChunkCoord(-1, chunkY, -1);
            var blocks = StreamingTerrainGenerator.Generate(42, coord);
            for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++)
            foreach (int y in new[] { 0, 15 })
                vertical &= blocks[(y * 16 + z) * 16 + x] ==
                    StreamingTerrainGenerator.GetBlock(42, coord.Origin + new Cell(x, y, z));
        }
        Check(vertical, "the four vertical sections agree with global terrain and tree samples at every vertical chunk face");

        Check(CrossBoundaryTree(), "a tree rooted outside a chunk contributes the same canopy when that neighboring chunk is generated alone");
        bool extremes = true;
        foreach (var coord in new[] { new ChunkCoord(ChunkCoord.Minimum, 0, ChunkCoord.Minimum), new ChunkCoord(ChunkCoord.Maximum, 0, ChunkCoord.Maximum) })
        {
            var blocks = StreamingTerrainGenerator.Generate(int.MinValue, coord);
            foreach (var local in new[] { new Cell(0, 0, 0), new Cell(15, 0, 15), new Cell(0, 15, 15), new Cell(15, 15, 0) })
                extremes &= blocks[(local.Y * 16 + local.Z) * 16 + local.X] ==
                    StreamingTerrainGenerator.GetBlock(int.MinValue, coord.Origin + local);
        }
        Check(extremes, "integer-limit chunk coordinates generate without overflow and match signed global samples");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { StreamingTerrainGenerator.Generate(731, default, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "a cancelled generation request cannot publish a completed chunk snapshot");
        bool invalid = false;
        try { StreamingTerrainGenerator.Generate(731, new(int.MaxValue, 0, 0)); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "chunk origins outside the integer Cell domain are rejected before arithmetic");
        Console.WriteLine($"Streaming terrain tests passed: {count}");
    }

    static bool CrossBoundaryTree()
    {
        const int seed = 731;
        for (int boundary = -48; boundary <= 48; boundary += 16)
        foreach (int rootX in new[] { boundary, boundary + 1, boundary + 14, boundary + 15 })
        for (int z = -48; z <= 48; z++)
        {
            int ground = StreamingTerrainGenerator.SurfaceHeight(seed, rootX, z);
            Block log = StreamingTerrainGenerator.GetBlock(seed, new(rootX, ground + 1, z));
            if (log is not (Block.OakLog or Block.BirchLog or Block.SpruceLog)) continue;
            int crown = ground + 1;
            while (crown < ground + 7 && StreamingTerrainGenerator.GetBlock(seed, new(rootX, crown + 1, z)) == log) crown++;
            int crossX = rootX - boundary < 2 ? boundary - 1 : boundary + 16;
            var canopy = new Cell(crossX, crown, z);
            Block leaves = StreamingTerrainGenerator.GetBlock(seed, canopy);
            if (leaves is not (Block.OakLeaves or Block.BirchLeaves or Block.SpruceLeaves)) continue;
            var leafChunk = ChunkCoord.FromWorld(canopy);
            var rootChunk = ChunkCoord.FromWorld(new(rootX, crown, z));
            if (leafChunk == rootChunk) continue;
            var alone = StreamingTerrainGenerator.Generate(seed, leafChunk);
            var rootBlocks = StreamingTerrainGenerator.Generate(seed, rootChunk);
            var leafLocal = ChunkCoord.ToLocal(canopy);
            var rootLocal = ChunkCoord.ToLocal(new(rootX, crown, z));
            return alone[(leafLocal.Y * 16 + leafLocal.Z) * 16 + leafLocal.X] == leaves &&
                rootBlocks[(rootLocal.Y * 16 + rootLocal.Z) * 16 + rootLocal.X] == log;
        }
        return false;
    }
}
