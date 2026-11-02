using System.Numerics;

namespace BlockCircuit;

static class WorldSessionTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++; Console.WriteLine("PASS: " + message);
        }
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-session-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "world.json");
            var original = new World();
            var edit = new Cell(20, 10, 20);
            original.Place(edit, Block.RedWool);
            original.Toggle(new(18, 10, 18));
            var before = original.Blocks;
            original.Save(path, original.Spawn(), .25f, -.3f);
            var result = WorldSession.Create(original, original.Spawn(), .25f, -.3f, 881, false, path);
            var backup = new World(false);
            var state = backup.Load(result.BackupPath);
            Check(backup.Blocks.SequenceEqual(before) && backup.Inventory.SequenceEqual(original.Inventory) &&
                backup.Power.GetValueOrDefault(new(24,10,18)) > 0 && state.Yaw == .25f,
                "new-world backup restores blocks, inventory, circuit power and camera");
            var reloaded = new World(false);
            reloaded.Load(path);
            Check(reloaded.Blocks.SequenceEqual(result.World.Blocks) && reloaded.Seed == 881 && reloaded.TerrainVersion == 1,
                "new terrain and seed persist in the active save");
            Check(original.Blocks.SequenceEqual(before) && original.Get(edit) == Block.RedWool,
                "creating a world leaves the previous in-memory world intact");
            bool failed = false;
            try { WorldSession.Create(original, original.Spawn(), 0, 0, 12, false, directory); }
            catch (IOException) { failed = true; }
            catch (UnauthorizedAccessException) { failed = true; }
            Check(failed && original.Blocks.SequenceEqual(before), "failed save cannot replace the active in-memory world");
            var preserved = new World(false); preserved.Load(path);
            Check(preserved.Blocks.SequenceEqual(result.World.Blocks), "failed new-world request preserves the last successful save");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"World session tests passed: {checks}");
    }
}
