using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

static class SelfTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            count++;
        }
        var ids = Enum.GetValues<Block>();
        Check(BlockCatalog.Placeable.Count >= 120 && BlockCatalog.Count < 256, "large catalog fits byte save IDs");
        Check(ids.Length == BlockCatalog.Count && ids.Select((b, i) => (int)b == i && BlockCatalog.Get(b).Id == b).All(ok => ok), "every stable enum ID maps to its metadata");
        Check((int)Block.Coal == 4 && (int)Block.Wire == 6 && (int)Block.Lamp == 8 && (int)Block.Bedrock == 9, "original block IDs preserved");
        Check(BlockCatalog.Placeable.Select(b => b.Name).Distinct().Count() == BlockCatalog.Placeable.Count &&
              BlockCatalog.Placeable.All(b => !string.IsNullOrWhiteSpace(b.Name) && float.IsFinite(b.Hardness) && b.Hardness > 0), "placeable blocks have unique names and finite mining durations");
        Check(BlockCatalog.Placeable.Count(b => b.Category == BlockCategory.Wool) == 16 &&
              BlockCatalog.Placeable.Count(b => b.Category == BlockCategory.Concrete) == 16 &&
              BlockCatalog.Placeable.Count(b => b.Category == BlockCategory.Terracotta) == 16 &&
              BlockCatalog.Placeable.Count(b => b.Category == BlockCategory.Glass) == 17, "all sixteen color sets and clear glass available");
        Check(BlockCatalog.CreateInventory().Length == BlockCatalog.Count && BlockCatalog.Placeable.All(b => BlockCatalog.CreateInventory()[(int)b.Id] == 128), "starter inventory includes every placeable block");

        var w = new World(false);
        for (int x = 1; x < 25; x++) w.Set(new(x, 1, 3), Block.Stone);
        var lever = new Cell(1, 2, 3);
        w.Set(lever, Block.Lever);
        for (int x = 2; x <= 16; x++) w.Set(new(x, 2, 3), Block.Wire);
        w.Set(new(5, 2, 4), Block.Lamp);
        w.Set(new(5, 2, 5), Block.Wire);
        w.Toggle(lever);
        Check(w.Power.GetValueOrDefault(new(5, 2, 4)) == 10, "branched lamp receives signal");
        Check(!w.Power.ContainsKey(new(5, 2, 5)), "lamps do not relay signals");
        Check(w.Power.GetValueOrDefault(new(15, 2, 3)) == 1 && !w.Power.ContainsKey(new(16, 2, 3)), "signal attenuation stops at zero");
        w.Toggle(lever);
        Check(w.Power.Count == 0, "lever off clears circuit");
        w.Toggle(lever);
        w.Mine(new(3, 2, 3));
        Check(!w.Power.ContainsKey(new(5, 2, 4)), "broken wire disconnects lamp");
        Check(w.Place(new(3, 2, 3), Block.Wire) && w.Power.ContainsKey(new(5, 2, 4)), "replacing wire restores power");
        int before = w.Inventory[(int)Block.Wire];
        w.Mine(new(3, 1, 3));
        Check(w.Get(new(3, 2, 3)) == Block.Air && w.Inventory[(int)Block.Wire] == before + 1, "unsupported wire reclaimed");
        Check(!w.Place(new(30, 5, 30), Block.Wire), "floating wire rejected");
        w.Set(new(5, 0, 5), Block.Bedrock);
        Check(!w.Mine(new(5, 0, 5)), "bedrock protected");
        var ray = w.Raycast(new Vector3(1.5f, 2.5f, 7), -Vector3.UnitZ);
        Check(ray?.Hit == lever && ray?.Previous == new Cell(1, 2, 4), "raycast returns placement neighbor");
        Check(!w.Place(new(2, 4, 2), (Block)255) && !w.Place(new(2, 4, 2), Block.Air) && !w.Place(new(2, 4, 2), Block.Bedrock), "invalid and non-placeable IDs rejected without inventory access");
        bool invalidSetRejected = false;
        try { w.Set(new(2, 4, 2), (Block)255); } catch (ArgumentOutOfRangeException) { invalidSetRejected = true; }
        Check(invalidSetRejected && w.Get(new(2, 4, 2)) == Block.Air, "direct invalid block mutation rejected");
        var glass = new Cell(28, 2, 28);
        Check(w.Place(glass, Block.Glass) && w.Solid(glass) && BlockCatalog.Get(Block.Glass).Transparent && w.Place(glass + new Cell(0, 1, 0), Block.Lever), "transparent glass remains collidable and supports circuits");
        w.Mine(glass);
        Check(w.Get(glass + new Cell(0, 1, 0)) == Block.Air, "mining glass reclaims supported lever");

        var allBlocks = new World(false);
        bool everyPlace = true, everyMine = true;
        int index = 0;
        foreach (var info in BlockCatalog.Placeable)
        {
            var cell = new Cell(2 + index % 30, 2, 2 + index / 30);
            allBlocks.Set(cell + new Cell(0, -1, 0), Block.Stone);
            int stock = allBlocks.Inventory[(int)info.Id];
            everyPlace &= allBlocks.Place(cell, info.Id) && allBlocks.Get(cell) == info.Id && allBlocks.Inventory[(int)info.Id] == stock - 1;
            everyMine &= allBlocks.Mine(cell) && allBlocks.Get(cell) == Block.Air && allBlocks.Inventory[(int)info.Id] == stock;
            everyPlace &= allBlocks.Place(cell, info.Id);
            index++;
        }
        Check(everyPlace, $"all {BlockCatalog.Placeable.Count} block types can be placed with correct inventory debit");
        Check(everyMine, "every placeable block can be mined and recovered");

        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-test-" + Guid.NewGuid() + ".json");
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        try
        {
            var position = new Vector3(10, 12, 10);
            allBlocks.Save(path, position, .3f, -.2f);
            var loaded = new World(false);
            var state = loaded.Load(path);
            Check(loaded.Blocks.SequenceEqual(allBlocks.Blocks) && loaded.Inventory.SequenceEqual(allBlocks.Inventory) && state.Position == position && state.Yaw == .3f && state.Pitch == -.2f, "all block types survive v2 save/load roundtrip");
            Check(JsonSerializer.Deserialize<World.SaveData>(File.ReadAllText(path), jsonOptions)?.Version == 2, "new saves use version 2");

            int[] legacyInventory = [0, 45, 27, 12, 9, 7, 44, 4, 13, 0];
            var legacy = new World.SaveData(1, w.Blocks, w.Switches.ToArray(), legacyInventory, position, .3f, -.2f);
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, jsonOptions));
            loaded.Load(path);
            Check(loaded.Blocks.SequenceEqual(w.Blocks) && loaded.Inventory.Take(10).SequenceEqual(legacyInventory) && loaded.Switches.SetEquals(w.Switches), "v1 migration preserves blocks, original inventory, and lever state");
            Check(loaded.Inventory.Length == BlockCatalog.Count && BlockCatalog.Placeable.Where(b => (int)b.Id >= 10).All(b => loaded.Inventory[(int)b.Id] == 128), "v1 migration grants starter stock for newly added blocks");
            Check(loaded.Power.Count == w.Power.Count && w.Power.All(pair => loaded.Power.GetValueOrDefault(pair.Key) == pair.Value), "v1 migration restores active circuit power");
            loaded.Save(path, position, .3f, -.2f);
            var valid = JsonSerializer.Deserialize<World.SaveData>(File.ReadAllText(path), jsonOptions)!;
            byte[] previousBlocks = loaded.Blocks.ToArray();
            int[] previousInventory = loaded.Inventory.ToArray();
            var previousSwitches = loaded.Switches.ToHashSet();
            var previousPower = new Dictionary<Cell, int>(loaded.Power);
            bool RejectedWithoutMutation(string json)
            {
                File.WriteAllText(path, json);
                try { loaded.Load(path); return false; }
                catch (InvalidDataException) { }
                return loaded.Blocks.SequenceEqual(previousBlocks) && loaded.Inventory.SequenceEqual(previousInventory) &&
                       loaded.Switches.SetEquals(previousSwitches) && loaded.Power.Count == previousPower.Count &&
                       previousPower.All(pair => loaded.Power.GetValueOrDefault(pair.Key) == pair.Value);
            }
            Check(RejectedWithoutMutation("{}") && RejectedWithoutMutation("null") && RejectedWithoutMutation("{broken"), "missing and malformed save data rejected atomically");
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Blocks = null }, jsonOptions)) &&
                  RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Inventory = null }, jsonOptions)) &&
                  RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Switches = null }, jsonOptions)), "null save arrays rejected atomically");
            var invalidBlocks = valid.Blocks!.ToArray();
            invalidBlocks[0] = 255;
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Blocks = invalidBlocks }, jsonOptions)), "unknown saved block ID rejected atomically");
            var invalidInventory = valid.Inventory!.ToArray();
            invalidInventory[(int)Block.DiamondBlock] = -1;
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Inventory = invalidInventory }, jsonOptions)) &&
                  RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Inventory = [1, 2, 3] }, jsonOptions)), "negative and incorrectly sized inventories rejected atomically");
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Version = 100 }, jsonOptions)) &&
                  RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Blocks = [1, 2, 3] }, jsonOptions)), "unknown versions and wrong world sizes rejected atomically");
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Switches = [new(-1, 0, 0)] }, jsonOptions)) &&
                  RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Switches = [new(35, 20, 35)] }, jsonOptions)), "invalid switch locations rejected atomically");
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(valid with { Position = new(-1, 12, 10) }, jsonOptions)), "invalid saved player position rejected atomically");
            invalidBlocks = w.Blocks.ToArray();
            invalidBlocks[0] = (byte)Block.DiamondBlock;
            Check(RejectedWithoutMutation(JsonSerializer.Serialize(legacy with { Blocks = invalidBlocks }, jsonOptions)), "v1 saves cannot contain new IDs");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }

        var terrain = new World();
        Check(terrain.Get(new(20, 9, 20)) == Block.Grass && terrain.Get(new(20, 10, 23)) == Block.Air &&
              terrain.Get(new(18, 10, 18)) == Block.Lever && terrain.Get(new(24, 10, 18)) == Block.Lamp, "spawn and demonstration circuit stay clear");
        Block[] ores = [Block.Coal, Block.Iron, Block.CopperOre, Block.GoldOre, Block.RedstoneOre, Block.LapisOre, Block.DiamondOre, Block.EmeraldOre];
        Check(ores.All(b => terrain.Blocks.Contains((byte)b)), "all eight overworld ore types generated");
        Check(new[] { Block.Granite, Block.Diorite, Block.Andesite, Block.Deepslate, Block.Tuff }.All(b => terrain.Blocks.Contains((byte)b)), "multiple underground stone varieties generated");
        Check(new[] { Block.OakLog, Block.BirchLog, Block.SpruceLog, Block.JungleLog, Block.AcaciaLog, Block.DarkOakLog, Block.MangroveLog, Block.CherryLog }.All(b => terrain.Blocks.Contains((byte)b)), "all eight tree species generated");
        Check(terrain.Blocks.Count(b => BlockCatalog.Get((Block)b).Pattern == BlockPattern.Leaves) > 100, "tree canopies generated");
        Check(terrain.Blocks.SequenceEqual(new World().Blocks), "terrain generation remains deterministic");

        var palette = new BlockPaletteModel();
        Check(palette.Filtered().Length == BlockCatalog.Placeable.Count && palette.Hotbar.Length == 8, "library exposes full catalog with eight hotbar slots");
        palette.Category = BlockCategory.Glass;
        palette.Query = "  bLuE  ";
        var blueGlass = palette.Filtered();
        Check(blueGlass.Length == 2 && blueGlass.All(b => b.Category == BlockCategory.Glass && b.Name.Contains("Blue")), "library combines category and case-insensitive trimmed search");
        palette.SelectedSlot = 4;
        Check(palette.Assign(Block.CherryPlanks) && palette.Hotbar[4] == Block.CherryPlanks && palette.Selected == Block.CherryPlanks, "choosing a block assigns the selected hotbar slot");
        Check(!palette.Assign(Block.Air) && !palette.Assign(Block.Bedrock) && !palette.Assign((Block)255) && palette.Selected == Block.CherryPlanks, "library rejects non-placeable and invalid IDs");
        Console.WriteLine($"All {count} checks passed.");
    }
}
