using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

/// <summary>Content identity, registration lifecycle, events, and persistent-ID acceptance checks.</summary>
static class RegistryTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            passed++;
        }
        TestIdentifiers(Check);
        TestRegistry(Check);
        TestBootstrapAndRuntimeBlocks(Check);
        TestEvents(Check);
        TestReentrantEditsAndPayloadHistory(Check);
        TestPersistentIdentity(Check);
        TestMissingContent(Check);
        Console.WriteLine($"Registry tests passed: {passed}");
    }

    static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (KeyNotFoundException) { return true; }
    }

    static BlockInfo Custom(string name) => new(Block.Air, name, BlockCategory.Stone,
        0xA0B0C0, BlockPattern.Stone, .5f);

    static bool SameJson(JsonElement? actual, JsonElement expected) => actual.HasValue &&
        JsonNode.DeepEquals(JsonNode.Parse(actual.Value.GetRawText()), JsonNode.Parse(expected.GetRawText()));

    static void TestIdentifiers(Action<bool, string> check)
    {
        var id = new ContentId("example:machines/logic_gate.v2");
        check(id.ToString() == "example:machines/logic_gate.v2" && id == new ContentId(id.ToString()),
            "content identifiers preserve their explicit namespace and path");
        string[] invalid = ["", "stone", ":stone", "example:", "Example:stone", "example:Stone", "example:two words", "example:stone:other", "example:stone\\other"];
        check(invalid.All(value => Rejected(() => new ContentId(value))),
            "missing namespaces, empty components, uppercase, whitespace, and invalid separators are rejected");
        check(new ContentId("alpha:stone") != new ContentId("beta:stone"),
            "equal paths in different namespaces remain distinct content");
    }

    static void TestRegistry(Action<bool, string> check)
    {
        var registry = new Registry<string>();
        var one = new ContentId("test:one");
        var two = new ContentId("test:two");
        int first = registry.Register(one, "first");
        int second = registry.Register(two, "second");
        check(first == 0 && second == 1 && registry.Count == 2 && !registry.IsFrozen,
            "registration assigns contiguous integer runtime IDs before freeze");
        check(registry.Get(first) == "first" && registry.Get(two) == "second" &&
              registry.GetId(second) == two && registry.GetRuntimeId(one) == first,
            "runtime and persistent identity lookups resolve the same registered values");
        check(Rejected(() => registry.Register(one, "replacement")) && registry.Count == 2 && registry.Get(one) == "first",
            "duplicate registration is rejected without changing the original entry");
        check(Rejected(() => registry.Register(default, "invalid")) && registry.Count == 2,
            "a default uninitialized content identifier cannot enter a Registry");
        check(!registry.TryGetRuntimeId(new ContentId("test:missing"), out _) &&
              Rejected(() => registry.Get(new ContentId("test:missing"))) &&
              Rejected(() => registry.Get(-1)) && Rejected(() => registry.Get(2)),
            "missing identifiers and out-of-range runtime IDs do not silently resolve to other content");
        registry.Freeze();
        registry.Freeze();
        check(registry.IsFrozen && Rejected(() => registry.Register(new ContentId("test:late"), "late")) && registry.Count == 2,
            "freeze is repeatable and prevents late registration");

        var reordered = new Registry<string>();
        reordered.Register(two, "second");
        reordered.Register(one, "first");
        reordered.Freeze();
        check(registry.GetRuntimeId(one) != reordered.GetRuntimeId(one) && registry.Get(one) == reordered.Get(one),
            "persistent identifiers resolve the same content despite runtime registration order changes");
    }

    static void TestBootstrapAndRuntimeBlocks(Action<bool, string> check)
    {
        var defaults = GameRegistries.Default;
        check(defaults.Blocks.Count == 181 && defaults.Items.Count == 180 && defaults.EntityTypes.IsFrozen,
            "core bootstrap retains legacy blocks and registers the crafting table and stick before freezing");
        check(Enum.GetValues<Block>().Where(block => (int)block >= 0 && (int)block < 180)
                .All(block => defaults.Blocks.GetInfo(block).Id == block && defaults.Blocks.GetBlock(defaults.Blocks.GetId(block)) == block),
            "all legacy block constants resolve through the same built-in Registry");
        check(Rejected(() => defaults.Blocks.Register(new ContentId("test:late_block"), Custom("Late"))),
            "the default block Registry is frozen before gameplay starts");
        Block large = Block.Air;
        var extended = GameRegistries.Create(registries =>
        {
            for (int i = 0; i < 300; i++)
                large = registries.Blocks.Register(new ContentId($"extension:block_{i}"), Custom($"Extension {i}"));
            registries.Items.Register(new ContentId("extension:component"), new ItemDefinition("Component"));
            registries.EntityTypes.Register(new ContentId("extension:machine"), new EntityTypeDefinition("Machine"));
        });
        check((int)large > 255 && extended.Blocks.GetInfo(large).Id == large && extended.Blocks.Count == 481,
            "runtime block identities and metadata support more than 255 registered blocks");
        check(extended.Items.Get(new ContentId("extension:component")).Name == "Component" &&
              extended.EntityTypes.Get(new ContentId("extension:machine")).Name == "Machine",
            "item and entity-type extension registrations participate in bootstrap");

        var world = new World(false, registries: extended);
        Cell cell = new(4, 2, 4);
        world.Inventory[(int)large] = 3;
        check(world.Place(cell, large) && world.Get(cell) == large && world.Inventory[(int)large] == 2 && world.Solid(cell),
            "a greater-than-byte block can be placed, collide, and debit inventory without ID truncation");
        var mesh = ChunkMesher.Build(world, ChunkCoord.FromWorld(cell));
        check(mesh.ExposedFaceCount == 6 && mesh.QuadCount == 6,
            "a registered mod block has valid fallback geometry without byte atlas overflow");
        check(world.Undo() && world.Get(cell) == Block.Air && world.Inventory[(int)large] == 3 &&
              world.Redo() && world.Get(cell) == large && world.Inventory[(int)large] == 2,
            "mod block placement undo and redo preserve full runtime identity and exact quantities");
        check(world.Mine(cell) && world.Get(cell) == Block.Air && world.Inventory[(int)large] == 3,
            "mining a greater-than-byte block returns the original item quantity");
        check(Rejected(() => world.Set(cell, (Block)(-1))) && Rejected(() => world.Set(cell, (Block)1000000)),
            "negative and unregistered runtime block IDs are rejected before world mutation");
    }

    static void TestEvents(Action<bool, string> check)
    {
        var events = new EventHub();
        int first = 0, second = 0;
        using var failing = events.Subscribe<int>(_ => throw new InvalidOperationException("extension failure"));
        IDisposable? removable = null;
        using var remover = events.Subscribe<int>(_ => { first++; removable?.Dispose(); });
        removable = events.Subscribe<int>(_ => second++);
        events.Publish(1);
        events.Publish(2);
        check(first == 2 && second == 1 && events.Errors.Count == 2,
            "event snapshots permit unsubscribe during dispatch and isolate failing extension listeners");
        remover.Dispose();
        events.Publish(3);
        check(first == 2 && second == 1,
            "disposed event subscriptions do not receive later dispatches");

        var orderedEvents = new EventHub();
        var order = new List<string>();
        Action<int> repeated = _ => order.Add("shared");
        using var firstRepeated = orderedEvents.Subscribe(repeated);
        using var middle = orderedEvents.Subscribe<int>(_ => order.Add("middle"));
        using var secondRepeated = orderedEvents.Subscribe(repeated);
        secondRepeated.Dispose();
        orderedEvents.Publish(0);
        check(order.SequenceEqual(new[] { "shared", "middle" }),
            "disposing a duplicate delegate subscription removes its own registration and preserves listener order");

        var world = new World(false);
        var changes = new List<BlockChangedEvent>();
        var placed = new List<BlockPlacedEvent>();
        var broken = new List<BlockBrokenEvent>();
        var ticks = new List<GameTickEvent>();
        using var changedSubscription = world.Events.Subscribe<BlockChangedEvent>(changes.Add);
        using var placedSubscription = world.Events.Subscribe<BlockPlacedEvent>(placed.Add);
        using var brokenSubscription = world.Events.Subscribe<BlockBrokenEvent>(broken.Add);
        using var tickSubscription = world.Events.Subscribe<GameTickEvent>(ticks.Add);
        Cell cell = new(7, 2, 7);
        world.Place(cell, Block.Stone);
        world.Place(cell, Block.Dirt);
        world.Set(cell, Block.Stone);
        world.Mine(cell);
        world.Mine(cell);
        check(changes.Count == 2 && changes[0].Previous == Block.Air && changes[0].Current == Block.Stone &&
              changes[1].Previous == Block.Stone && changes[1].Current == Block.Air,
            "block changes publish original and final identities while no-op edits publish nothing");
        check(placed.Count == 1 && broken.Count == 1 && placed[0].Block == Block.Stone && broken[0].Block == Block.Stone,
            "only successful placement and mining publish semantic block events");
        world.Tick();
        world.Tick();
        check(ticks.Count == 2 && ticks[0].Tick == 1 && ticks[1].Tick == 2 && ReferenceEquals(ticks[0].World, world),
            "world game-tick events are ordered and identify the owning world");
    }

    static void TestReentrantEditsAndPayloadHistory(Action<bool, string> check)
    {
        var world = new World(false);
        Cell outer = new(10, 2, 10), inner = new(11, 2, 10);
        int stone = world.Inventory[(int)Block.Stone], dirt = world.Inventory[(int)Block.Dirt];
        bool handled = false, nestedSucceeded = false;
        using var subscription = world.Events.Subscribe<BlockChangedEvent>(change =>
        {
            if (change.Cell == outer && change.Current == Block.Stone && !handled)
            {
                handled = true;
                nestedSucceeded = world.Place(inner, Block.Dirt);
            }
        });
        bool outerSucceeded = world.Place(outer, Block.Stone);
        check(outerSucceeded && nestedSucceeded && world.Get(outer) == Block.Stone && world.Get(inner) == Block.Dirt &&
              world.Inventory[(int)Block.Stone] == stone - 1 && world.Inventory[(int)Block.Dirt] == dirt - 1 && world.UndoCount == 2,
            "an extension may place a second block from a change listener without corrupting either edit transaction");
        check(world.Undo() && world.Get(inner) == Block.Air && world.Get(outer) == Block.Stone &&
              world.Inventory[(int)Block.Dirt] == dirt && world.Undo() && world.Get(outer) == Block.Air &&
              world.Inventory[(int)Block.Stone] == stone && world.UndoCount == 0,
            "listener-triggered and original edits undo independently in commit order with exact quantities");

        Block machine = Block.Air;
        var installed = GameRegistries.Create(registries =>
            machine = registries.Blocks.Register(new ContentId("history:machine"), Custom("History Machine")));
        var stateful = new World(false, registries: installed);
        Cell cell = new(12, 2, 12);
        stateful.Set(cell, machine);
        stateful.Inventory[(int)machine] = 4;
        using var payload = JsonDocument.Parse("{\"charge\":17,\"owner\":\"history\"}");
        stateful.SetBlockData(cell, payload.RootElement);
        check(stateful.Mine(cell) && stateful.Inventory[(int)machine] == 5 && stateful.Undo() &&
              stateful.Get(cell) == machine && stateful.Inventory[(int)machine] == 4 && SameJson(stateful.GetBlockData(cell), payload.RootElement),
            "mining undo restores a mod block's opaque data together with its identity and inventory");
        check(stateful.Redo() && stateful.Get(cell) == Block.Air && stateful.GetBlockData(cell) is null &&
              stateful.Inventory[(int)machine] == 5 && stateful.Undo() && SameJson(stateful.GetBlockData(cell), payload.RootElement),
            "redo removes restored payloads and a second undo restores the same state without duplication");
    }

    static void TestPersistentIdentity(Action<bool, string> check)
    {
        Block firstAlpha = Block.Air, firstBeta = Block.Air;
        var first = GameRegistries.Create(registries =>
        {
            firstAlpha = registries.Blocks.Register(new ContentId("reorder:alpha"), Custom("Alpha"));
            firstBeta = registries.Blocks.Register(new ContentId("reorder:beta"), Custom("Beta"));
        });
        Block secondAlpha = Block.Air, secondBeta = Block.Air;
        var second = GameRegistries.Create(registries =>
        {
            secondBeta = registries.Blocks.Register(new ContentId("reorder:beta"), Custom("Beta"));
            secondAlpha = registries.Blocks.Register(new ContentId("reorder:alpha"), Custom("Alpha"));
        });
        var source = new World(false, registries: first);
        Cell alpha = new(3, 2, 3), beta = new(4, 2, 3);
        source.Set(alpha, firstAlpha);
        source.Set(beta, firstBeta);
        source.Inventory[(int)firstAlpha] = 7;
        source.Inventory[(int)firstBeta] = 11;
        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-registry-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            source.ExportSnapshot(path, source.Spawn(), .3f, -.2f);
            var loaded = new World(false, registries: second);
            var state = loaded.Load(path);
            check(firstAlpha != secondAlpha && firstBeta != secondBeta && loaded.Get(alpha) == secondAlpha && loaded.Get(beta) == secondBeta,
                "saved block palettes resolve persistent names after mod registration order changes");
            check(loaded.Inventory[(int)secondAlpha] == 7 && loaded.Inventory[(int)secondBeta] == 11 &&
                  state.Position == source.Spawn() && state.Yaw == .3f && state.Pitch == -.2f,
                "persistent item quantities and player state survive reordered Registry round trips");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            check(document.RootElement.GetProperty("Version").GetInt32() == 3 && File.ReadAllText(path).Contains("reorder:alpha", StringComparison.Ordinal),
                "standalone registry snapshots use v3 persistent-ID storage rather than runtime IDs alone");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    static void TestMissingContent(Action<bool, string> check)
    {
        var identifier = new ContentId("optional:machine");
        Block machine = Block.Air;
        var installed = GameRegistries.Create(registries =>
            machine = registries.Blocks.Register(identifier, Custom("Optional Machine")));
        var original = new World(false, registries: installed);
        Cell cell = new(8, 2, 8);
        original.Set(cell, machine);
        original.Inventory[(int)machine] = 23;
        using var data = JsonDocument.Parse("{\"state\":{\"charge\":17,\"facing\":\"west\"},\"custom\":[true,2,null]}");
        original.SetBlockData(cell, data.RootElement);
        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-missing-content-" + Guid.NewGuid().ToString("N") + ".json");
        string roundtrip = path + ".roundtrip.json";
        try
        {
            original.ExportSnapshot(path, original.Spawn(), 0, 0);
            var missing = new World(false);
            missing.Load(path);
            check(missing.Get(cell) == Block.Stone && missing.GetPersistentId(cell) == identifier &&
                  SameJson(missing.GetBlockData(cell), data.RootElement),
                "missing mod blocks use a solid fallback while retaining the original identifier and opaque data");
            check(missing.UnknownInventory.TryGetValue(identifier.ToString(), out int quantity) && quantity == 23,
                "items from absent mods retain their named quantities instead of becoming another item");
            missing.ExportSnapshot(roundtrip, original.Spawn(), 0, 0);
            var restored = new World(false, registries: installed);
            restored.Load(roundtrip);
            check(restored.Get(cell) == machine && restored.GetPersistentId(cell) == identifier &&
                  restored.Inventory[(int)machine] == 23 && SameJson(restored.GetBlockData(cell), data.RootElement),
                "reinstalling a mod restores its block, item quantity, and original opaque state after resaving without it");

            string valid = File.ReadAllText(path);
            bool malformedRejected = true;
            foreach (string corruption in new[] { "missing-palette", "bad-id", "bad-index", "short-cells", "null-payload" })
            {
                var invalid = JsonNode.Parse(valid)!.AsObject();
                switch (corruption)
                {
                    case "missing-palette": invalid["Palette"] = null; break;
                    case "bad-id": invalid["Palette"]!.AsArray()[0] = "invalid identifier"; break;
                    case "bad-index": invalid["Cells"]!.AsArray()[0] = int.MaxValue; break;
                    case "short-cells": invalid["Cells"] = new JsonArray(0, 1); break;
                    case "null-payload": invalid["BlockData"] = new JsonArray((JsonNode?)null); break;
                }
                File.WriteAllText(roundtrip, invalid.ToJsonString());
                bool rejected = false;
                try { restored.Load(roundtrip); }
                catch (InvalidDataException) { rejected = true; }
                malformedRejected &= rejected && restored.Get(cell) == machine && restored.Inventory[(int)machine] == 23 &&
                    SameJson(restored.GetBlockData(cell), data.RootElement);
            }
            check(malformedRejected,
                "invalid palettes, identifiers, cell indices, and cell counts are rejected before changing live state");
        }
        finally
        {
            File.Delete(path); File.Delete(path + ".tmp");
            File.Delete(roundtrip); File.Delete(roundtrip + ".tmp");
        }
    }
}
