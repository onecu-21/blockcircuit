using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

/// <summary>Falling-block conservation, motion, scheduling, streaming and persistence checks.</summary>
static class FallingBlockTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        Check(BlockCatalog.Placeable.Where(info => info.Id is Block.Sand or Block.RedSand or Block.Gravel).All(info => info.AffectedByGravity),
            "the legacy catalog view exposes the same gravity metadata as the content Registry");
        TestKindsAndMotion(Check);
        TestWakeAndStack(Check);
        TestBudgets(Check);
        TestUnavailableAndLanding(Check);
        TestPersistenceAndPayload(Check);
        TestEventsAndHistory(Check);
        Console.WriteLine($"Falling-block tests passed: {passed}");
    }

    static void Step(FallingBlockSystem system, int ticks = 100)
    { for (int i = 0; i < ticks; i++) system.Tick(); }
    static int Count(World world, FallingBlockSystem system, Block block) =>
        world.OccupiedCells().Count(cell => world.Get(cell) == block) + system.Active.Count(actor => actor.Block == block);
    static void Floor(World world, int x, int z) => world.Set(new(x, 0, z), Block.Stone);
    static bool SameData(JsonElement? actual, JsonElement expected) => actual.HasValue &&
        JsonNode.DeepEquals(JsonNode.Parse(actual.Value.GetRawText()), JsonNode.Parse(expected.GetRawText()));
    static bool Rejected(Action operation)
    { try { operation(); return false; } catch (InvalidDataException) { return true; } }

    static void TestKindsAndMotion(Action<bool, string> check)
    {
        var world = new World(false);
        var system = new FallingBlockSystem(world);
        Block[] falling = [Block.Sand, Block.RedSand, Block.Gravel];
        check(falling.All(block => world.Registries.Blocks.GetInfo(block).AffectedByGravity) &&
            !world.Registries.Blocks.GetInfo(Block.Stone).AffectedByGravity &&
            !world.Registries.Blocks.GetInfo(Block.WhiteConcrete).AffectedByGravity &&
            !world.Registries.Blocks.GetInfo(Block.Dripstone).AffectedByGravity,
            "sand, red sand and gravel are gravity blocks while solid concrete and dripstone blocks remain supported types");
        foreach (var (block, index) in falling.Select((block, index) => (block, index)))
        {
            Floor(world, 3 + index, 3);
            world.Set(new(3 + index, 5, 3), block);
            system.Schedule(new(3 + index, 5, 3));
        }
        int[] inventory = world.Inventory.ToArray();
        system.Tick();
        check(system.Active.Count == 3 && falling.All(block => Count(world, system, block) == 1) &&
              Math.Abs(system.Active[0].Position.Y - 4.96f) < .00001f &&
              Math.Abs(system.Active[0].VelocityY + .0392f) < .000001f &&
              system.Active[0].PreviousPosition.Y == 5,
            "unsupported blocks detach once and begin continuous 20 TPS falling with an interpolatable previous position");
        Step(system);
        check(system.Active.Count == 0 && falling.Select((block, i) => world.Get(new(3 + i, 1, 3)) == block).All(ok => ok),
            "each gravity block settles on the first solid surface as its original block type");
        check(world.Inventory.SequenceEqual(inventory) && falling.All(block => Count(world, system, block) == 1),
            "detaching, falling and settlement do not duplicate blocks or change inventory quantities");
        world.Set(new(8, 5, 3), Block.Stone); system.Schedule(new(8, 5, 3)); Step(system);
        check(world.Get(new(8, 5, 3)) == Block.Stone && system.Active.Count == 0,
            "ordinary unsupported stone does not acquire gravity");
        system.Schedule(new(int.MaxValue, 5, 0));
        system.Schedule(new(1, -1, 1));
        check(system.PendingCount == 0, "unsupported simulation coordinates do not create invalid saved scheduler entries");
    }

    static void TestWakeAndStack(Action<bool, string> check)
    {
        var world = new World(false);
        Floor(world, 6, 6);
        world.Set(new(6, 3, 6), Block.Stone);
        world.Set(new(6, 4, 6), Block.Sand);
        var system = new FallingBlockSystem(world);
        system.OnChunkLoaded(new(0, 0, 0));
        Step(system, 5);
        check(world.Get(new(6, 4, 6)) == Block.Sand && system.Active.Count == 0 && system.PendingCount == 0,
            "a gravity block remains static while supported");
        Step(system, 25);
        check(system.LastScheduledChecks == 0 && system.LastActiveUpdates == 0,
            "an idle falling system does not scan the world on subsequent ticks");
        world.Set(new(6, 3, 6), Block.Air);
        system.OnCellChanged(new(6, 3, 6));
        Step(system);
        check(world.Get(new(6, 1, 6)) == Block.Sand && world.Get(new(6, 4, 6)) == Block.Air,
            "removing support wakes the block immediately above the changed cell");
        var stacked = new World(false);
        Floor(stacked, 10, 10);
        for (int y = 4; y <= 6; y++) stacked.Set(new(10, y, 10), Block.Sand);
        var stackSystem = new FallingBlockSystem(stacked);
        stackSystem.OnChunkLoaded(new(0, 0, 0));
        bool conserved = true;
        for (int i = 0; i < 120; i++) { stackSystem.Tick(); conserved &= Count(stacked, stackSystem, Block.Sand) == 3; }
        check(conserved && stackSystem.Active.Count == 0 &&
            Enumerable.Range(1, 3).All(y => stacked.Get(new(10, y, 10)) == Block.Sand),
            "a falling column lands as three distinct stacked blocks without overlap, loss or duplication");
    }

    static void TestBudgets(Action<bool, string> check)
    {
        var world = new World(false);
        for (int x = 0; x < 16; x++) for (int z = 0; z < 4; z++)
        { Floor(world, x, z); world.Set(new(x, 8, z), Block.Gravel); }
        var system = new FallingBlockSystem(world) { MaxScheduledChecksPerTick = 3, MaxActiveUpdatesPerTick = 2 };
        system.OnChunkLoaded(new(0, 0, 0));
        system.Tick();
        check(system.LastScheduledChecks == 3 && system.LastActiveUpdates == 2 && system.PendingCount >= 61,
            "scheduled checks and active falling updates obey independent per-tick work budgets");
        system.MaxScheduledChecksPerTick = 512; system.MaxActiveUpdatesPerTick = 256;
        Step(system, 150);
        check(Count(world, system, Block.Gravel) == 64 && system.Active.Count == 0,
            "deferred work eventually settles every block while retaining all 64 gravel blocks");
        system.MaxActiveUpdatesPerTick = 0;
        bool rejected = false;
        try { system.Tick(); } catch (InvalidOperationException) { rejected = true; }
        system.MaxActiveUpdatesPerTick = 256;
        check(rejected, "nonpositive work budgets are rejected rather than silently stopping simulation");
    }

    static void TestUnavailableAndLanding(Action<bool, string> check)
    {
        var world = new World(false);
        Floor(world, -1, -1);
        world.Set(new(-1, 17, -1), Block.RedSand);
        bool lowerLoaded = false;
        var system = new FallingBlockSystem(world, cell => cell.Y >= 16 || lowerLoaded);
        system.OnChunkLoaded(new(-1, 1, -1));
        Step(system, 60);
        check(system.Active.Count == 1 && system.Active[0].Position.Y >= 16 &&
            Count(world, system, Block.RedSand) == 1,
            "a falling block pauses at an unavailable lower chunk instead of disappearing through missing terrain");
        check(system.PinnedChunks.Contains(new(-1, 1, -1)) && system.PinnedChunks.Contains(new(-1, 0, -1)),
            "active falling blocks identify current and next chunk ownership to prevent unsafe unloading");
        lowerLoaded = true; Step(system);
        check(system.Active.Count == 0 && world.Get(new(-1, 1, -1)) == Block.RedSand,
            "falling resumes across the vertical chunk boundary once terrain is available");
        var guarded = new World(false);
        Floor(guarded, 12, 12); guarded.Set(new(12, 5, 12), Block.Sand);
        bool allowed = false;
        var guardedSystem = new FallingBlockSystem(guarded) { CanLand = _ => allowed };
        guardedSystem.Schedule(new(12, 5, 12)); Step(guardedSystem);
        check(guardedSystem.Active.Count == 1 && guarded.Get(new(12, 1, 12)) == Block.Air,
            "a blocked player landing cell retains its falling block rather than deleting or overwriting it");
        allowed = true; Step(guardedSystem, 2);
        check(guardedSystem.Active.Count == 0 && guarded.Get(new(12, 1, 12)) == Block.Sand,
            "settlement retries successfully after the landing obstruction clears");
        var parts = new World(false);
        Floor(parts, 14, 14);
        parts.Set(new(14, 1, 14), Block.Wire); parts.Set(new(14, 5, 14), Block.Gravel);
        int wireCount = parts.Inventory[(int)Block.Wire];
        var partsSystem = new FallingBlockSystem(parts); partsSystem.Schedule(new(14, 5, 14)); Step(partsSystem);
        check(parts.Get(new(14, 1, 14)) == Block.Gravel && parts.Inventory[(int)Block.Wire] == wireCount + 1 &&
            Count(parts, partsSystem, Block.Gravel) == 1,
            "settlement recovers an obstructing wire through normal mining without losing the falling block");
    }

    static void TestPersistenceAndPayload(Action<bool, string> check)
    {
        Block custom = Block.Air;
        var registries = GameRegistries.Create(r =>
        {
            for (int i = 0; i < 300; i++)
                custom = r.Blocks.Register(new ContentId($"gravity:ore_{i}"),
                    new(Block.Air, "Heavy ore " + i, BlockCategory.Ores, 0x887766, BlockPattern.Ore, AffectedByGravity: true));
        });
        var source = new World(false, registries: registries);
        Floor(source, 7, 7); source.Set(new(7, 6, 7), custom);
        using var payload = JsonDocument.Parse("{\"charge\":17,\"state\":[true,2,null]}");
        source.SetBlockData(new(7, 6, 7), payload.RootElement);
        var system = new FallingBlockSystem(source); system.Schedule(new(7, 6, 7)); system.Tick();
        var options = new JsonSerializerOptions { IncludeFields = true };
        string json = JsonSerializer.Serialize(system.Capture(), options);
        var captured = JsonSerializer.Deserialize<FallingBlockSaveState>(json, options)!;
        var unknownWorld = new World(false);
        Floor(unknownWorld, 7, 7);
        var unknown = new FallingBlockSystem(unknownWorld); unknown.Restore(captured);
        Vector3 paused = unknown.Active[0].Position;
        Step(unknown, 20);
        check(unknown.Active.Count == 1 && unknown.Active[0].Block == Block.Stone && unknown.Active[0].Position == paused &&
            unknown.Active[0].PersistentId == registries.Blocks.GetId(custom) && SameData(unknown.Active[0].Data, payload.RootElement),
            "an unavailable mod's active falling block retains its persistent identifier and opaque state while suspended");
        var restoredWorld = new World(false, registries: registries);
        Floor(restoredWorld, 7, 7);
        var restored = new FallingBlockSystem(restoredWorld); restored.Restore(unknown.Capture());
        Guid id = restored.Active[0].Id;
        check((int)custom > 255 && id == system.Active[0].Id && restored.Active[0].Position == system.Active[0].Position &&
            restored.Active[0].VelocityY == system.Active[0].VelocityY,
            "serialized mid-fall state restores full block identity, stable actor ID, position and velocity");
        Step(restored);
        check(restored.Active.Count == 0 && restoredWorld.Get(new(7, 1, 7)) == custom &&
            SameData(restoredWorld.GetBlockData(new(7, 1, 7)), payload.RootElement),
            "reinstalling a mod lets its saved falling block settle with the original payload");
        system.Restore(captured);
        var good = system.Capture();
        var sample = good.Active[0];
        FallingBlockSaveState[] invalid =
        [
            good with { Version = 99 },
            new(null!, []),
            new([sample with { Id = Guid.Empty }], []),
            new([sample, sample], []),
            new([sample with { Identifier = "bad id" }], []),
            new([sample with { Identifier = "blockcircuit:air" }], []),
            new([sample with { PreviousPosition = sample.PreviousPosition + Vector3.UnitX }], []),
            new([sample with { Position = new(float.NaN, 4, 7.5f) }], []),
            new([sample with { VelocityY = float.NegativeInfinity }], []),
            new([sample, sample with { Id = Guid.NewGuid() }], []),
            new([], [new(1, -1, 1)]),
            new([], [new(1, 2, 1), new(1, 2, 1)])
        ];
        check(invalid.All(state => Rejected(() => system.Restore(state))) && system.Active.Count == 1 &&
            system.Active[0].Id == sample.Id && SameData(system.Active[0].Data, payload.RootElement),
            "malformed falling states are rejected before replacing a valid active simulation");
    }

    static void TestEventsAndHistory(Action<bool, string> check)
    {
        var world = new World(false);
        Floor(world, 4, 4);
        world.Place(new(4, 5, 4), Block.Sand);
        int inventory = world.Inventory[(int)Block.Sand];
        var system = new FallingBlockSystem(world);
        system.Schedule(new(4, 5, 4));
        bool coherent = true;
        using var listener = world.Events.Subscribe<BlockChangedEvent>(_ => coherent &= Count(world, system, Block.Sand) == 1);
        system.Tick();
        check(world.UndoCount == 0 && !world.Undo() && world.Inventory[(int)Block.Sand] == inventory,
            "automatic falling invalidates stale edit history instead of allowing the original source to be duplicated by undo");
        Step(system);
        check(coherent && world.Get(new(4, 1, 4)) == Block.Sand,
            "block-change listeners observe exactly one static or active representation during detach and settlement");
        using var payload = JsonDocument.Parse("{\"tag\":\"kept\"}");
        world.Set(new(4, 5, 4), Block.Gravel);
        world.SetBlockData(new(4, 5, 4), payload.RootElement);
        bool observedPayload = false;
        using var payloadListener = world.Events.Subscribe<BlockChangedEvent>(change =>
        {
            if (change.Current == Block.Gravel && change.Cell.Y == 1)
                observedPayload = SameData(world.GetBlockData(change.Cell), payload.RootElement) && system.Active.All(a => a.Block != Block.Gravel);
        });
        // Move this second block over its own floor rather than overwrite the landed sand.
        world.Set(new(4, 1, 4), Block.Air);
        system.Schedule(new(4, 5, 4)); Step(system);
        check(observedPayload && world.Get(new(4, 1, 4)) == Block.Gravel,
            "settlement notifications publish only after the actor is removed and opaque block data is restored");
    }
}
