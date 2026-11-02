using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

public static class TerrainTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        var noise = new TerrainNoise(731);
        Check(noise.Noise3(1.25, -3.75, 8.125) == new TerrainNoise(731).Noise3(1.25, -3.75, 8.125),
            "noise is repeatable with the same seed");
        Check(noise.Noise3(1.25, -3.75, 8.125) != new TerrainNoise(732).Noise3(1.25, -3.75, 8.125),
            "different seeds change the noise field");
        bool bounded = true;
        for (int i = -30; i <= 30; i++)
            bounded &= noise.Noise3(i * .137, i * -.213, i * .531) is >= -1 and <= 1 &&
                       noise.Fractal2(i * .117, i * -.321) is >= -1 and <= 1;
        Check(bounded, "negative-coordinate 3D noise and normalized fBm stay within bounds");
        Check(Math.Abs(noise.Noise2(1 - .000001, -.75) - noise.Noise2(1 + .000001, -.75)) < .0001,
            "noise remains continuous across a lattice boundary");

        var legacy = new World();
        Check(legacy.Blocks.SequenceEqual(new World(seed: 731, legacyTerrain: true).Blocks),
            "the default constructor retains the original terrain");
        Check(legacy.Seed == 731 && legacy.TerrainVersion == 0, "legacy terrain provenance is explicit");
        var first = new World(seed: 731, legacyTerrain: false);
        var repeat = new World(seed: 731, legacyTerrain: false);
        Check(first.Blocks.SequenceEqual(repeat.Blocks), "the whole seeded world is deterministic");
        Check(!first.Blocks.SequenceEqual(new World(seed: 732, legacyTerrain: false).Blocks),
            "changing the seed changes terrain, caves, ores and vegetation");
        Check(!first.Blocks.SequenceEqual(legacy.Blocks), "improved generation differs from the legacy sine terrain");

        foreach (int seed in new[] { 731, 42, 2026, -1, int.MinValue, int.MaxValue })
        {
            var world = seed == 731 ? first : new World(seed: seed, legacyTerrain: false);
            bool bedrock = true, roof = true, rooted = true, clearing = true;
            int caves = 0, trunks = 0;
            var heights = new HashSet<int>();
            for (int x = 0; x < World.Size; x++) for (int z = 0; z < World.Size; z++)
            {
                bedrock &= world.Get(new(x, 0, z)) == Block.Bedrock;
                int top = Surface(world, x, z);
                heights.Add(top);
                roof &= top is >= 5 and <= World.Height - 6 &&
                    world.Get(new(x, top - 1, z)) != Block.Air &&
                    world.Get(new(x, top - 2, z)) != Block.Air;
                for (int y = 2; y < top - 2; y++) if (world.Get(new(x, y, z)) == Block.Air) caves++;
                for (int y = 1; y < World.Height; y++)
                    if (IsLog(world.Get(new(x, y, z))))
                    {
                        trunks++;
                        var below = world.Get(new(x, y - 1, z));
                        rooted &= IsLog(below) || below is Block.Grass or Block.Moss;
                    }
                if (x >= 16 && x <= 26 && z >= 16 && z <= 26)
                    clearing &= world.Get(new(x, 9, z)) == Block.Grass;
            }
            Check(bedrock, $"seed {seed}: bedrock remains continuous");
            Check(roof && heights.Count >= 5, $"seed {seed}: terrain varies in height and cave roofs remain intact");
            Check(caves > 0, $"seed {seed}: underground caves are generated");
            Check(rooted && trunks > 0, $"seed {seed}: tree trunks have terrain or trunk support");
            Check(clearing && !CollisionSystem.Collides(world, world.Spawn()),
                $"seed {seed}: spawn clearing and player headroom are safe");
            Check(world.Get(new(18, 10, 18)) == Block.Lever && world.Get(new(24, 10, 18)) == Block.Lamp &&
                world.Toggle(new(18, 10, 18)) && world.Power.GetValueOrDefault(new(24, 10, 18)) == 9,
                $"seed {seed}: the example circuit remains connected and functional");
        }

        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-terrain-" + Guid.NewGuid().ToString("N") + ".json");
        var options = new JsonSerializerOptions { IncludeFields = true };
        try
        {
            var saved = new World(seed: int.MinValue, legacyTerrain: false);
            saved.Toggle(new(18, 10, 18));
            saved.Save(path, saved.Spawn(), 0, 0);
            var loaded = new World(false);
            loaded.Load(path);
            Check(saved.Blocks.SequenceEqual(loaded.Blocks) && loaded.Seed == int.MinValue && loaded.TerrainVersion == 1,
                "save/load preserves block snapshots and terrain seed metadata");
            Check(loaded.Power.GetValueOrDefault(new(24, 10, 18)) == 9,
                "loading seeded terrain restores circuit power");

            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["TerrainVersion"] = -1;
            File.WriteAllText(path, node.ToJsonString());
            byte[] before = loaded.Blocks;
            bool rejected = false;
            try { loaded.Load(path); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && loaded.Blocks.SequenceEqual(before) && loaded.Seed == int.MinValue && loaded.TerrainVersion == 1,
                "invalid generation metadata is rejected without altering the active world");

            legacy.Save(path, legacy.Spawn(), 0, 0);
            node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node.Remove("Seed"); node.Remove("TerrainVersion");
            File.WriteAllText(path, node.ToJsonString());
            loaded.Load(path);
            Check(loaded.Seed == 731 && loaded.TerrainVersion == 0 && loaded.Blocks.SequenceEqual(legacy.Blocks),
                "v2 saves without optional terrain metadata retain compatibility");

            var v1 = new World.SaveData(1, new byte[World.Size * World.Size * World.Height], [], new int[10],
                new Vector3(20.5f, 10.01f, 23.5f), 0, 0);
            File.WriteAllText(path, JsonSerializer.Serialize(v1, options));
            loaded.Load(path);
            Check(loaded.Seed == 731 && loaded.TerrainVersion == 0 && loaded.Blocks.All(b => b == 0),
                "original v1 block snapshots still load");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
        Console.WriteLine($"Terrain tests: {passed} passed.");
    }

    static bool IsLog(Block b) => b is Block.OakLog or Block.BirchLog or Block.SpruceLog;
    static bool IsTree(Block b) => IsLog(b) || b is Block.OakLeaves or Block.BirchLeaves or Block.SpruceLeaves;
    static int Surface(World world, int x, int z)
    {
        for (int y = World.Height - 1; y >= 0; y--)
        {
            var b = world.Get(new(x, y, z));
            if (b != Block.Air && !IsTree(b) && b is not (Block.Wire or Block.Lever or Block.Lamp)) return y;
        }
        return -1;
    }
}
