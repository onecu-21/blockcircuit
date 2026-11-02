using System.Numerics;
using System.Security.Cryptography;

namespace BlockCircuit;

/// <summary>Headless engine checks. No window, GL context, or Raylib native calls required.</summary>
static class EngineTests
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

        int[] coordinates = [-65, -33, -32, -17, -16, -15, -1, 0, 1, 15, 16, 17, 31, 32, 65];
        foreach (int x in coordinates)
        foreach (int y in coordinates)
        foreach (int z in coordinates)
        {
            var cell = new Cell(x, y, z);
            var chunk = ChunkCoord.FromWorld(cell);
            var local = ChunkCoord.ToLocal(cell);
            if (chunk.X != (int)Math.Floor(x / 16d) || chunk.Y != (int)Math.Floor(y / 16d) || chunk.Z != (int)Math.Floor(z / 16d) ||
                local.X is < 0 or >= 16 || local.Y is < 0 or >= 16 || local.Z is < 0 or >= 16 || chunk.Origin + local != cell)
                throw new InvalidOperationException($"Coordinate roundtrip failed at {cell}.");
        }
        Check(true, "chunk/local coordinates use floor division and roundtrip across negative boundaries");

        var manager = new ChunkManager();
        manager.Set(new(-17, -1, 16), Block.Air);
        Check(manager.Chunks.Count == 0, "writing air to absent chunks does not allocate storage");
        manager.Set(new(-17, -1, 16), Block.Stone);
        var negativeCoord = new ChunkCoord(-2, -1, 1);
        Check(manager.Get(new(-17, -1, 16)) == Block.Stone && manager.Chunks[negativeCoord].NonAirCount == 1,
            "sparse chunks store blocks at negative world coordinates");
        long firstRevision = manager.Chunks[negativeCoord].Revision;
        manager.Set(new(-17, -1, 16), Block.Stone);
        Check(manager.Chunks[negativeCoord].Revision == firstRevision, "unchanged block writes preserve the cached revision");
        manager.Set(new(-17, -1, 16), Block.Air);
        Check(manager.Chunks.Count == 0 && manager.Get(new(-17, -1, 16)) == Block.Air, "removing the last block releases a sparse chunk");

        var center = new ChunkCoord(0, 0, 0);
        manager.Set(new(8, 8, 8), Block.Stone);
        foreach (Cell offset in World.Neighbors)
            manager.Set(new(offset.X * 16 + 8, offset.Y * 16 + 8, offset.Z * 16 + 8), Block.Stone);
        var before = Revisions(manager);
        manager.Set(new(8, 8, 8), Block.Dirt);
        Check(Changed(before, manager).SequenceEqual([center]), "interior edits invalidate only their owner chunk");
        Cell[] boundaryCells = [new(15, 8, 8), new(0, 8, 8), new(8, 15, 8), new(8, 0, 8), new(8, 8, 15), new(8, 8, 0)];
        bool boundariesCorrect = true;
        for (int face = 0; face < 6; face++)
        {
            before = Revisions(manager);
            manager.Set(boundaryCells[face], Block.Stone);
            Cell offset = World.Neighbors[face];
            var expected = new HashSet<ChunkCoord> { center, new(offset.X, offset.Y, offset.Z) };
            boundariesCorrect &= expected.SetEquals(Changed(before, manager));
        }
        Check(boundariesCorrect, "all six boundary edits invalidate their existing neighboring chunk");
        before = Revisions(manager);
        manager.Set(new(0, 0, 0), Block.Stone);
        Check(new HashSet<ChunkCoord> { center, new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1) }.SetEquals(Changed(before, manager)),
            "corner edits invalidate the three face neighbors without diagonal chunks");
        before = Revisions(manager);
        manager.Set(new(0, 0, 0), Block.Stone);
        Check(!Changed(before, manager).Any(), "no-op boundary writes do not dirty any neighboring chunk");
        long deletedRevision = manager.Chunks[center].Revision;
        before = Revisions(manager);
        manager.Remove(center);
        Check(manager.Chunks.Count == 6 && Changed(before, manager).Count() == 6,
            "removing a whole chunk invalidates all six existing face neighbors");
        manager.Set(new(8, 8, 8), Block.Stone);
        Check(manager.Chunks[center].Revision > deletedRevision,
            "recreating a chunk cannot accidentally reuse a stale cached revision");

        var terrain = new World();
        Check(terrain.Blocks.SequenceEqual(new World().Blocks), "chunk-backed terrain generation is deterministic");
        // This digest is captured from the pre-chunk generator, not derived from the new implementation.
        const string expectedTerrainHash = "48563C0F5486EC9E67CC186343958F2EFD814D21A25BF78921816DF12E4525D7";
        string terrainHash = Convert.ToHexString(SHA256.HashData(terrain.Blocks));
        Check(terrainHash == expectedTerrainHash, "chunk-backed terrain matches the original flat-world snapshot byte for byte");
        byte[] snapshot = terrain.Blocks;
        Block original = terrain.Get(new(0, 0, 0));
        snapshot[0] = (byte)Block.Air;
        Check(terrain.Get(new(0, 0, 0)) == original, "legacy serialization snapshots cannot bypass chunk invalidation");

        TestRaycasts(Check);
        TestMeshing(Check);
        TestFrustum(Check);
        TestSerialization(Check);
        Console.WriteLine($"All {count} engine checks passed (headless).");
    }

    static Dictionary<ChunkCoord, long> Revisions(ChunkManager manager) => manager.Chunks.ToDictionary(p => p.Key, p => p.Value.Revision);
    static IEnumerable<ChunkCoord> Changed(Dictionary<ChunkCoord, long> old, ChunkManager current) =>
        current.Chunks.Where(p => !old.TryGetValue(p.Key, out long revision) || revision != p.Value.Revision).Select(p => p.Key);

    static void TestRaycasts(Action<bool, string> check)
    {
        var world = new World(false);
        var block = new Cell(8, 8, 8);
        world.Set(block, Block.Stone);
        bool axes = true;
        foreach (Cell offset in World.Neighbors)
        {
            var direction = new Vector3(offset.X, offset.Y, offset.Z);
            var hit = world.Raycast(block.Center - direction * 3, direction);
            var expectedPrevious = new Cell(block.X - offset.X, block.Y - offset.Y, block.Z - offset.Z);
            axes &= hit?.Hit == block && hit?.Previous == expectedPrevious;
        }
        check(axes, "DDA hits all six axis directions and returns the face-adjacent placement voxel");
        check(world.Raycast(new(8.5f, 8.5f, 5), new(0, 0, 20), 3)?.Hit == block &&
              world.Raycast(new(8.5f, 8.5f, 5), Vector3.UnitZ, 2.999f) is null,
            "DDA normalizes direction, includes exact range endpoint, and rejects a hit beyond range");
        check(world.Raycast(block.Center, Vector3.UnitX)?.Hit == block && world.Raycast(block.Center, Vector3.UnitX)?.Previous == block,
            "DDA reports an occupied origin voxel immediately");
        var faceHit = world.Raycast(new(9, 8.5f, 8.5f), -Vector3.UnitX);
        check(faceHit?.Hit == block && faceHit?.Previous == new Cell(9, 8, 8), "DDA steps correctly from an exact integer face in the negative direction");
        check(world.Raycast(new(-2, 8.5f, 8.5f), Vector3.UnitX, 10)?.Hit == block,
            "DDA traverses negative empty coordinates before entering the bounded world");
        check(world.Raycast(block.Center, Vector3.Zero) is null &&
              world.Raycast(block.Center, new(float.NaN, 0, 1)) is null &&
              world.Raycast(block.Center, new(float.PositiveInfinity, 0, 1)) is null &&
              world.Raycast(new(float.NaN, 0, 1), Vector3.UnitX) is null &&
              world.Raycast(block.Center, Vector3.UnitX, 0) is null &&
              world.Raycast(block.Center, Vector3.UnitX, -1) is null &&
              world.Raycast(block.Center, Vector3.UnitX, float.PositiveInfinity) is null,
            "DDA rejects zero/invalid directions, invalid origins, and nonpositive/nonfinite ranges");
        var tieWorld = new World(false);
        tieWorld.Set(new(3, 3, 3), Block.Stone);
        var corner = tieWorld.Raycast(new(2.5f, 2.5f, 2.5f), Vector3.One);
        check(corner?.Hit == new Cell(3, 3, 3) && corner?.Previous == new Cell(3, 3, 2),
            "DDA corner ties follow deterministic X/Y/Z order with an adjacent placement voxel");
        tieWorld.Set(new(3, 2, 2), Block.Dirt);
        check(tieWorld.Raycast(new(2.5f, 2.5f, 2.5f), Vector3.One)?.Hit == new Cell(3, 2, 2),
            "DDA tie policy checks intermediate face-adjacent cells");
        var thin = new World(false);
        thin.Set(new(1, 3, 3), Block.Stone);
        check(thin.Raycast(new(0, 3.5f, 3.5f), new(1, 0.000001f, 0), 2)?.Hit == new Cell(1, 3, 3),
            "DDA handles nearly axis-aligned rays without fixed-step gaps");
    }

    static void TestMeshing(Action<bool, string> check)
    {
        var coord = new ChunkCoord(0, 0, 0);
        var solid = new World(false);
        for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++)
            solid.Set(new(x, y, z), Block.Stone);
        var single = ChunkMesher.Build(solid, coord);
        check(single.QuadCount == 6 && single.ExposedFaceCount == 6 * 16 * 16 && single.Opaque.VertexCount == 36,
            "greedy meshing reduces an isolated solid chunk to six quads");
        check(ValidateMesh(solid, coord, single), "greedy quads preserve every oriented unit face, material, light, and tiled UV extent");
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) solid.Set(new(16, y, z), Block.Stone);
        var seam = ChunkMesher.Build(solid, coord);
        check(seam.QuadCount == 5 && seam.ExposedFaceCount == 5 * 16 * 16 && ValidateMesh(solid, coord, seam),
            "meshing reads adjacent chunks and removes hidden seam faces");
        solid.Set(new(16, 8, 8), Block.Air);
        seam = ChunkMesher.Build(solid, coord);
        check(seam.ExposedFaceCount == 5 * 16 * 16 + 1 && ValidateMesh(solid, coord, seam),
            "breaking a neighboring boundary block exposes exactly one owner face");

        var checker = new World(false);
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++)
            checker.Set(new(x, 5, z), ((x + z) & 1) == 0 ? Block.Stone : Block.Dirt);
        check(ValidateMesh(checker, coord, ChunkMesher.Build(checker, coord)), "checkerboard materials do not merge across texture boundaries");

        var materials = new World(false);
        Block[] types = [Block.Grass, Block.OakLog, Block.Stone, Block.Glowstone, Block.Glass, Block.BlueGlass, Block.OakLeaves, Block.Lamp];
        for (int x = 0; x < types.Length; x++) for (int z = 0; z < 4; z++) materials.Set(new(x + 2, 6, z + 2), types[x]);
        materials.Set(new(8, 7, 8), Block.Lever);
        materials.Set(new(9, 7, 8), Block.Wire);
        materials.Set(new(10, 7, 8), Block.Lamp);
        materials.Toggle(new(8, 7, 8));
        var mixed = ChunkMesher.Build(materials, coord);
        check(ValidateMesh(materials, coord, mixed), "greedy keys preserve face texture variants, direction, lamp state, light, and transparency");
        check(mixed.Opaque.VertexCount > 0 && mixed.Transparent.VertexCount > 0 &&
              mixed.Circuits.ToHashSet().SetEquals([new Cell(8, 7, 8), new Cell(9, 7, 8)]),
            "opaque/transparent geometry is separated and small circuit models stay outside voxel meshes");
        var oldMesh = ChunkMesher.Build(materials, coord, greedy: false);
        check(UnitFaces(mixed).SetEquals(UnitFaces(oldMesh)) && mixed.QuadCount <= oldMesh.QuadCount,
            "greedy and unmerged meshes cover identical faces with identical material/light data");
        var circuitWorld = new World(false);
        circuitWorld.Set(new(14, 8, 8), Block.Lever);
        circuitWorld.Set(new(15, 8, 8), Block.Wire);
        circuitWorld.Set(new(16, 8, 8), Block.Wire);
        circuitWorld.Set(new(17, 8, 8), Block.Lamp);
        circuitWorld.Set(new(35, 5, 35), Block.Stone);
        var circuitRevisions = Revisions(circuitWorld.Chunks);
        circuitWorld.Toggle(new(14, 8, 8));
        check(circuitWorld.Power.GetValueOrDefault(new(17, 8, 8)) == 12 &&
              new HashSet<ChunkCoord> { new(0, 0, 0), new(1, 0, 0) }.SetEquals(Changed(circuitRevisions, circuitWorld.Chunks)) &&
              ValidateMesh(circuitWorld, new(1, 0, 0), ChunkMesher.Build(circuitWorld, new(1, 0, 0))),
            "power changes across chunk seams invalidate affected circuit visuals without dirtying unrelated chunks");

        var random = new Random(93452);
        var fuzz = new World(false);
        for (int i = 0; i < 700; i++)
            fuzz.Set(new(random.Next(0, 33), random.Next(0, 24), random.Next(0, 33)), types[random.Next(types.Length)]);
        check(fuzz.Chunks.Chunks.Keys.All(c => ValidateMesh(fuzz, c, ChunkMesher.Build(fuzz, c))),
            "seeded mixed-block scenes match an independent exposed-face oracle in every chunk");
        var empty = ChunkMesher.Build(new World(false), coord);
        check(empty.QuadCount == 0 && empty.Opaque.VertexCount == 0 && empty.Transparent.VertexCount == 0 && empty.Circuits.Length == 0,
            "absent chunks produce no geometry or circuits");
    }

    // Rasterize each emitted rectangle into oriented unit faces, independently of the mesher's mask algorithm.
    // Tile coordinates and light are vertex attributes consumed by the atlas shader.
    readonly record struct UnitFace(Cell Cell, int Face, byte TileX, byte TileY, byte Light, bool Transparent);

    static HashSet<UnitFace> ExpectedFaces(World world, ChunkCoord coord)
    {
        var result = new HashSet<UnitFace>();
        Cell origin = coord.Origin;
        float[] directionalLight = [.79f, .87f, 1f, .53f, .91f, .71f];
        for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++)
        {
            Cell cell = origin + new Cell(x, y, z);
            Block block = world.Get(cell);
            if (block is Block.Air or Block.Wire or Block.Lever) continue;
            bool powered = block == Block.Lamp && world.Power.GetValueOrDefault(cell) > 0;
            bool emissive = powered || block is Block.Glowstone or Block.SeaLantern or Block.MagmaBlock;
            for (int face = 0; face < 6; face++)
            {
                Block neighbor = world.Get(cell + World.Neighbors[face]);
                bool visible = neighbor is Block.Air or Block.Wire or Block.Lever ||
                    (BlockCatalog.Get(neighbor).Transparent && block != neighbor);
                if (!visible) continue;
                int variant = powered ? 3 : face == 2 ? 1 : face == 3 ? 2 : 0;
                int tile = (int)block * 4 + variant;
                byte light = (byte)(255 * (emissive ? .95f : directionalLight[face]));
                bool transparent = BlockCatalog.Get(block).Pattern == BlockPattern.Glass;
                result.Add(new(cell, face, (byte)(tile % 32), (byte)(tile / 32), light, transparent));
            }
        }
        return result;
    }

    static bool ValidateMesh(World world, ChunkCoord coord, ChunkMeshData mesh)
    {
        var expected = ExpectedFaces(world, coord);
        var actual = UnitFaces(mesh);
        return expected.Count == mesh.ExposedFaceCount && expected.SetEquals(actual);
    }

    static HashSet<UnitFace> UnitFaces(ChunkMeshData mesh)
    {
        var result = new HashSet<UnitFace>();
        Add(mesh.Opaque, false);
        Add(mesh.Transparent, true);
        if (mesh.QuadCount * 6 != mesh.Opaque.VertexCount + mesh.Transparent.VertexCount)
            throw new InvalidOperationException("Mesh vertex/quad counts disagree.");
        return result;

        void Add(CpuMeshData source, bool transparent)
        {
            if (source.Positions.Length != source.VertexCount * 3 || source.TexCoords.Length != source.VertexCount * 2 ||
                source.Colors.Length != source.VertexCount * 4 || source.VertexCount % 6 != 0)
                throw new InvalidOperationException("Invalid mesh attribute lengths.");
            Vector3 Point(int vertex) => new(source.Positions[vertex * 3], source.Positions[vertex * 3 + 1], source.Positions[vertex * 3 + 2]);
            Vector3[] uTangents = [-Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, -Vector3.UnitX];
            Vector3[] vTangents = [-Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitY];
            for (int i = 0; i < source.VertexCount; i += 6)
            {
                Vector3 a = Point(i), b = Point(i + 1), c = Point(i + 2);
                Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                int face = Array.FindIndex(World.Neighbors, n => Vector3.DistanceSquared(normal, new(n.X, n.Y, n.Z)) < .00001f);
                if (face < 0) throw new InvalidOperationException("Mesh face must be axis aligned and nondegenerate.");
                Vector3 min = a, max = a;
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                for (int vertex = i; vertex < i + 6; vertex++)
                {
                    Vector3 p = Point(vertex);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                    if (p.X != MathF.Round(p.X) || p.Y != MathF.Round(p.Y) || p.Z != MathF.Round(p.Z))
                        throw new InvalidOperationException("Voxel quad vertices must be integer grid coordinates.");
                    for (int channel = 0; channel < 4; channel++)
                        if (source.Colors[vertex * 4 + channel] != source.Colors[i * 4 + channel])
                            throw new InvalidOperationException("A merged face crosses material or light boundaries.");
                    if (source.Colors[vertex * 4 + 3] != 255 ||
                        MathF.Abs(source.TexCoords[vertex * 2] - source.TexCoords[i * 2] - Vector3.Dot(p - a, uTangents[face])) > .001f ||
                        MathF.Abs(source.TexCoords[vertex * 2 + 1] - source.TexCoords[i * 2 + 1] - Vector3.Dot(p - a, vTangents[face])) > .001f)
                        throw new InvalidOperationException("Quad UV orientation, voxel tiling, or vertex alpha changed.");
                    minU = Math.Min(minU, source.TexCoords[vertex * 2]); maxU = Math.Max(maxU, source.TexCoords[vertex * 2]);
                    minV = Math.Min(minV, source.TexCoords[vertex * 2 + 1]); maxV = Math.Max(maxV, source.TexCoords[vertex * 2 + 1]);
                }
                Vector3 span = max - min;
                float area = (face / 2) switch { 0 => span.Y * span.Z, 1 => span.X * span.Z, _ => span.X * span.Y };
                if (MathF.Abs((maxU - minU) * (maxV - minV) - area) > .001f)
                    throw new InvalidOperationException("Merged face UVs stretch instead of repeating once per voxel.");
                Vector3 secondNormal = Vector3.Normalize(Vector3.Cross(Point(i + 4) - Point(i + 3), Point(i + 5) - Point(i + 3)));
                if (Vector3.DistanceSquared(normal, secondNormal) > .00001f)
                    throw new InvalidOperationException("Quad triangles have inconsistent winding.");
                float triangleArea = (Vector3.Cross(b - a, c - a).Length() +
                    Vector3.Cross(Point(i + 4) - Point(i + 3), Point(i + 5) - Point(i + 3)).Length()) / 2;
                if (MathF.Abs(triangleArea - area) > .001f)
                    throw new InvalidOperationException("Quad triangles do not cover their rectangle.");
                int x0 = (int)min.X, y0 = (int)min.Y, z0 = (int)min.Z;
                int x1 = (int)max.X, y1 = (int)max.Y, z1 = (int)max.Z;
                if (face / 2 == 0) { x0 -= face == 0 ? 1 : 0; x1 = x0 + 1; }
                if (face / 2 == 1) { y0 -= face == 2 ? 1 : 0; y1 = y0 + 1; }
                if (face / 2 == 2) { z0 -= face == 4 ? 1 : 0; z1 = z0 + 1; }
                for (int x = x0; x < x1; x++) for (int y = y0; y < y1; y++) for (int z = z0; z < z1; z++)
                    if (!result.Add(new(new(x, y, z), face, source.Colors[i * 4], source.Colors[i * 4 + 1], source.Colors[i * 4 + 2], transparent)))
                        throw new InvalidOperationException("Mesh contains duplicate unit faces.");
            }
        }
    }

    static void TestFrustum(Action<bool, string> check)
    {
        var frustum = new FrustumCuller(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 2, 1, .1f, 30);
        check(frustum.Intersects(new(-1, -1, -5), new(1, 1, -3)), "frustum keeps a chunk directly ahead");
        check(!frustum.Intersects(new(-1, -1, 3), new(1, 1, 5)), "frustum rejects a chunk behind the camera");
        check(!frustum.Intersects(new(20, -1, -5), new(22, 1, -3)) && !frustum.Intersects(new(-1, 20, -5), new(1, 22, -3)),
            "frustum rejects chunks beyond horizontal and vertical side planes");
        check(!frustum.Intersects(new(-1, -1, -35), new(1, 1, -31)), "frustum rejects chunks beyond far plane");
        check(frustum.Intersects(new(-1, -1, -1), new(1, 1, 1)) && frustum.Intersects(new(3.9f, -.1f, -4.1f), new(4.1f, .1f, -3.9f)),
            "frustum conservatively keeps boxes intersecting the camera or frustum edge");
    }

    static void TestSerialization(Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-chunk-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var world = new World(false);
            world.Set(new(0, 0, 0), Block.Bedrock);
            world.Set(new(16, 16, 16), Block.DiamondBlock);
            world.Set(new(39, 27, 39), Block.BlueGlass);
            world.Save(path, world.Spawn(), .25f, -.1f);
            var loaded = new World();
            loaded.Load(path);
            check(loaded.Blocks.SequenceEqual(world.Blocks) && loaded.Chunks.Chunks.Count == 3 &&
                  loaded.Chunks.Chunks.Values.Sum(c => c.NonAirCount) == 3,
                "legacy v2 save snapshots reload into exactly the required sparse chunks");
            var empty = new World(false);
            empty.Save(path, empty.Spawn(), 0, 0);
            loaded.Load(path);
            check(loaded.Chunks.Chunks.Count == 0 && loaded.Blocks.All(b => b == 0),
                "loading an empty world removes previously populated chunks");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
