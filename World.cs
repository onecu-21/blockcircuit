using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

public readonly record struct Cell(int X, int Y, int Z)
{
    public Vector3 Center => new(X + .5f, Y + .5f, Z + .5f);
    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}

public sealed class World
{
    public const int Size = 40, Height = 28;
    public ChunkManager Chunks { get; private set; } = new();
    // Legacy flattened format is materialized only for saves and compatibility checks.
    // Mutating this snapshot never mutates the live world or bypasses mesh invalidation.
    public byte[] Blocks
    {
        get
        {
            var snapshot = new byte[Size * Size * Height];
            for (int y = 0; y < Height; y++) for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
            {
                var cell = new Cell(x, y, z);
                snapshot[Index(cell)] = (byte)Chunks.Get(cell);
            }
            return snapshot;
        }
    }
    public HashSet<Cell> Switches { get; private set; } = [];
    public Dictionary<Cell, int> Power { get; } = [];
    public int[] Inventory { get; private set; } = BlockCatalog.CreateInventory();
    public CircuitSystem Circuits { get; }
    private readonly EditHistory history = new(128);
    private EditTransaction? currentEdit;
    private bool replayingHistory;
    public int UndoCount => history.UndoCount;
    public int RedoCount => history.RedoCount;
    public static readonly Cell[] Neighbors = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    public bool Inside(Cell p) => p.X >= 0 && p.X < Size && p.Y >= 0 && p.Y < Height && p.Z >= 0 && p.Z < Size;
    private static int Index(Cell p) => (p.Y * Size + p.Z) * Size + p.X;
    public Block Get(Cell p) => Inside(p) ? Chunks.Get(p) : Block.Air;
    public bool Solid(Cell p) => Get(p) is not (Block.Air or Block.Wire or Block.Lever);

    public void Set(Cell p, Block b)
    {
        if (!BlockCatalog.IsValid(b)) throw new ArgumentOutOfRangeException(nameof(b), "Unknown block ID.");
        if (!Inside(p)) return;
        var previous = new EditCellState(Get(p), Switches.Contains(p));
        if (previous.Block == b && (b == Block.Lever || !previous.SwitchOn)) return;
        if (currentEdit is null && !replayingHistory) history.Clear();
        Chunks.Set(p, b);
        if (b != Block.Lever) Switches.Remove(p);
        currentEdit?.RecordCell(p, previous, new(b, Switches.Contains(p)));
        if (previous.Block != b) Circuits.EnqueueBlockChange(p, previous.Block, b);
        else Circuits.EnqueueSwitchChange(p);
    }

    public World(bool generate = true)
    {
        Circuits = new CircuitSystem(this);
        if (!generate) return;
        var random = new Random(731);
        var surface = new int[Size, Size];
        for (int x = 0; x < Size; x++) for (int z = 0; z < Size; z++)
        {
            int top = 8 + (int)(Math.Sin(x * .22) * 1.7 + Math.Cos(z * .19) * 1.5);
            if (x > 13 && x < 27 && z > 13 && z < 27) top = 9;
            surface[x, z] = top;
            for (int y = 0; y <= top; y++)
            {
                Block b = y == 0 ? Block.Bedrock : y == top ? Block.Grass : y >= top - 2 ? Block.Dirt : RockAt(x, y, z, random);
                // A winding underground tunnel, with an unbreakable floor below it.
                if (y >= 2 && y <= 4 && Math.Abs(z - (12 + Math.Sin(x * .25) * 4)) < 2) b = Block.Air;
                Set(new(x, y, z), b);
            }
        }
        GenerateTrees(surface, random);
        Set(new(18, 10, 18), Block.Lever);
        for (int x = 19; x <= 23; x++) Set(new(x, 10, 18), Block.Wire);
        Set(new(24, 10, 18), Block.Lamp);
        RecomputePower();
    }

    private static Block RockAt(int x, int y, int z, Random random)
    {
        double ore = random.NextDouble();
        if (ore < .065) return Block.Coal;
        if (ore < .115) return Block.Iron;
        if (ore < .155) return Block.CopperOre;
        if (ore < .175) return Block.GoldOre;
        if (ore < .193) return Block.RedstoneOre;
        if (ore < .206) return Block.LapisOre;
        if (y <= 4 && ore < .215) return Block.DiamondOre;
        if (y <= 4 && ore < .222) return Block.EmeraldOre;
        return ((x / 5 + z / 5 * 3 + y / 3) % 11) switch
        {
            0 => Block.Granite,
            1 => Block.Diorite,
            2 => Block.Andesite,
            3 => Block.Tuff,
            _ => y <= 2 ? Block.Deepslate : Block.Stone
        };
    }

    private void GenerateTrees(int[,] surface, Random random)
    {
        (Block Log, Block Leaves)[] trees =
        [
            (Block.OakLog, Block.OakLeaves), (Block.BirchLog, Block.BirchLeaves),
            (Block.SpruceLog, Block.SpruceLeaves), (Block.JungleLog, Block.JungleLeaves),
            (Block.AcaciaLog, Block.AcaciaLeaves), (Block.DarkOakLog, Block.DarkOakLeaves),
            (Block.MangroveLog, Block.MangroveLeaves), (Block.CherryLog, Block.CherryLeaves)
        ];
        int treeIndex = 0;
        for (int gridX = 4; gridX < Size - 2; gridX += 8) for (int gridZ = 4; gridZ < Size - 2; gridZ += 8)
        {
            int x = gridX + random.Next(-1, 2), z = gridZ + random.Next(-1, 2);
            // Keep trunks and their canopies away from the flat spawn/circuit area.
            if (x >= 10 && x <= 29 && z >= 10 && z <= 29) continue;
            int ground = surface[x, z], trunkHeight = random.Next(3, 5);
            var tree = trees[treeIndex++ % trees.Length];
            for (int y = ground + 1; y <= ground + trunkHeight; y++) Set(new(x, y, z), tree.Log);
            int crown = ground + trunkHeight;
            for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++) for (int dy = -1; dy <= 1; dy++)
            {
                if (Math.Abs(dx) + Math.Abs(dz) > (dy == 1 ? 2 : 3)) continue;
                var p = new Cell(x + dx, crown + dy, z + dz);
                if (Inside(p) && Get(p) == Block.Air) Set(p, tree.Leaves);
            }
        }
    }

    public Vector3 Spawn() => new(20.5f, 10.01f, 23.5f);

    public void RecomputePower() => Circuits.Rebuild();

    public bool Toggle(Cell p)
    {
        if (Get(p) != Block.Lever) return false;
        return PerformEdit(() =>
        {
            var before = new EditCellState(Block.Lever, Switches.Contains(p));
            if (!Switches.Add(p)) Switches.Remove(p);
            currentEdit!.RecordCell(p, before, new(Block.Lever, Switches.Contains(p)));
            Circuits.EnqueueSwitchChange(p);
        });
    }

    public bool Mine(Cell p)
    {
        var b = Get(p);
        if (b is Block.Air or Block.Bedrock || Inventory[(int)b] == int.MaxValue) return false;
        return PerformEdit(() =>
        {
            ChangeInventory(b, 1);
            Set(p, Block.Air);
            RemoveUnsupportedAbove(p);
        });
    }

    public bool Place(Cell p, Block b)
    {
        if (!BlockCatalog.IsValid(b) || !Inside(p) || Get(p) != Block.Air || b is Block.Air or Block.Bedrock || Inventory[(int)b] <= 0) return false;
        if (b is Block.Wire or Block.Lever && !Solid(p + new Cell(0, -1, 0))) return false;
        return PerformEdit(() =>
        {
            Set(p, b);
            ChangeInventory(b, -1);
        });
    }

    private void RemoveUnsupportedAbove(Cell support)
    {
        var p = support + new Cell(0, 1, 0);
        while (Inside(p))
        {
            var b = Get(p);
            if (b is not (Block.Wire or Block.Lever) || Solid(p + new Cell(0, -1, 0))) break;
            if (Inventory[(int)b] < int.MaxValue) ChangeInventory(b, 1);
            Set(p, Block.Air);
            p += new Cell(0, 1, 0);
        }
    }

    private bool PerformEdit(Action action)
    {
        currentEdit = new EditTransaction();
        try
        {
            action();
            Circuits.ProcessPending();
            history.Record(currentEdit);
            return true;
        }
        finally { currentEdit = null; }
    }

    private void ChangeInventory(Block block, int delta)
    {
        Inventory[(int)block] += delta;
        currentEdit?.RecordInventory(block, delta);
    }

    public void ClearHistory() => history.Clear();
    public bool Undo(Func<Cell, Block, bool>? canRestore = null) => ReplayHistory(false, canRestore);
    public bool Redo(Func<Cell, Block, bool>? canRestore = null) => ReplayHistory(true, canRestore);

    private bool ReplayHistory(bool redo, Func<Cell, Block, bool>? canRestore)
    {
        var edit = redo ? history.RedoEntry : history.UndoEntry;
        if (edit is null) return false;
        int sign = redo ? 1 : -1;
        // Validate every cell and inventory delta before changing anything, including the history stacks.
        foreach (var change in edit.Cells.Values)
        {
            var expected = redo ? change.Before : change.After;
            var desired = redo ? change.After : change.Before;
            if (Get(change.Cell) != expected.Block || Switches.Contains(change.Cell) != expected.SwitchOn) return false;
            if (desired.Block != expected.Block && canRestore is not null && !canRestore(change.Cell, desired.Block)) return false;
        }
        foreach (var (block, delta) in edit.InventoryDeltas)
        {
            long next = Inventory[(int)block] + (long)sign * delta;
            if (next < 0 || next > int.MaxValue) return false;
        }

        replayingHistory = true;
        try
        {
            foreach (var change in edit.Cells.Values)
            {
                var desired = redo ? change.After : change.Before;
                Set(change.Cell, desired.Block);
                bool wasOn = Switches.Contains(change.Cell);
                if (desired.SwitchOn) Switches.Add(change.Cell);
                else Switches.Remove(change.Cell);
                if (wasOn != desired.SwitchOn) Circuits.EnqueueSwitchChange(change.Cell);
            }
            foreach (var (block, delta) in edit.InventoryDeltas) Inventory[(int)block] += sign * delta;
            Circuits.ProcessPending();
            if (redo) history.FinishRedo(); else history.FinishUndo();
            return true;
        }
        finally { replayingHistory = false; }
    }

    public (Cell Hit, Cell Previous)? Raycast(Vector3 origin, Vector3 direction, float range = 6)
        => VoxelRaycaster.Cast(Get, origin, direction, range);

    public record SaveData(int Version, byte[]? Blocks, Cell[]? Switches, int[]? Inventory, Vector3 Position, float Yaw, float Pitch);
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public void Save(string path, Vector3 position, float yaw, float pitch)
    {
        var data = new SaveData(2, Blocks, Switches.ToArray(), Inventory, position, yaw, pitch);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }

    public (Vector3 Position, float Yaw, float Pitch) Load(string path)
    {
        SaveData data;
        try
        {
            data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Empty save");
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid save format", e); }
        int expectedInventory = data.Version == 1 ? 10 : BlockCatalog.Count;
        int maxBlock = data.Version == 1 ? 9 : BlockCatalog.Count - 1;
        if (data.Version is not (1 or 2) || data.Blocks is null || data.Switches is null || data.Inventory is null ||
            data.Blocks.Length != Size * Size * Height || data.Inventory.Length != expectedInventory ||
            data.Blocks.Any(b => b > maxBlock) || data.Inventory.Any(n => n < 0) ||
            data.Switches.Any(p => !Inside(p) || data.Blocks[Index(p)] != (byte)Block.Lever) ||
            !float.IsFinite(data.Position.X) || !float.IsFinite(data.Position.Y) || !float.IsFinite(data.Position.Z) ||
            !float.IsFinite(data.Yaw) || !float.IsFinite(data.Pitch) ||
            data.Position.X < .3f || data.Position.X > Size - .3f || data.Position.Z < .3f || data.Position.Z > Size - .3f ||
            data.Position.Y < 0 || data.Position.Y > Height + 10)
            throw new InvalidDataException("Invalid save format");

        // Prepare and validate the entire migrated state before touching the current world.
        int[] inventory = BlockCatalog.CreateInventory();
        Array.Copy(data.Inventory, inventory, data.Inventory.Length);
        var switches = data.Switches.ToHashSet();
        var chunks = new ChunkManager();
        for (int y = 0; y < Height; y++) for (int z = 0; z < Size; z++) for (int x = 0; x < Size; x++)
        {
            var cell = new Cell(x, y, z);
            var block = (Block)data.Blocks[Index(cell)];
            if (block != Block.Air) chunks.Set(cell, block);
        }
        Chunks = chunks;
        Inventory = inventory;
        Switches = switches;
        RecomputePower();
        ClearHistory();
        return (data.Position, data.Yaw, Math.Clamp(data.Pitch, -1.5f, 1.5f));
    }
}
