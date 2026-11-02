using System.Numerics;

namespace BlockCircuit;

/// <summary>Headless behavioral checks for incremental simulation, editing, physics, and settings.</summary>
static class GameplayTests
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
        TestCircuits(Check);
        TestHistory(Check);
        TestClockAndPhysics(Check);
        TestSettings(Check);
        Console.WriteLine($"All {count} gameplay checks passed (headless).");
    }

    // Independent legacy behavior oracle: signal travels from active levers through wires;
    // lamps consume signal and inactive levers cannot relay it.
    internal static Dictionary<Cell, int> ReferencePower(World world, out int visited)
    {
        var result = new Dictionary<Cell, int>();
        var queue = new Queue<Cell>();
        visited = 0;
        foreach (Cell cell in world.Switches)
            if (world.Get(cell) == Block.Lever) { result[cell] = 15; queue.Enqueue(cell); }
        while (queue.TryDequeue(out Cell cell))
        {
            visited++;
            int signal = result[cell] - 1;
            if (signal <= 0) continue;
            foreach (Cell offset in World.Neighbors)
            {
                Cell next = cell + offset;
                Block block = world.Get(next);
                if (block is not (Block.Wire or Block.Lamp) || result.GetValueOrDefault(next) >= signal) continue;
                result[next] = signal;
                if (block == Block.Wire) queue.Enqueue(next);
            }
        }
        return result;
    }

    internal static bool SamePower(IReadOnlyDictionary<Cell, int> a, IReadOnlyDictionary<Cell, int> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out int value) && value == pair.Value);

    static void AddCircuit(World world, int startX, int z, int wireCount = 4)
    {
        for (int x = startX; x <= startX + wireCount + 1; x++) world.Set(new(x, 1, z), Block.Stone);
        world.Set(new(startX, 2, z), Block.Lever);
        for (int x = startX + 1; x <= startX + wireCount; x++) world.Set(new(x, 2, z), Block.Wire);
        world.Set(new(startX + wireCount + 1, 2, z), Block.Lamp);
    }

    static void TestCircuits(Action<bool, string> check)
    {
        var world = new World(false);
        AddCircuit(world, 2, 3);
        AddCircuit(world, 24, 24);
        check(world.Circuits.PendingEventCount > 0, "block edits queue circuit events before processing");
        world.Circuits.ProcessPending();
        check(world.Circuits.PendingEventCount == 0 && world.Circuits.NetworkCount == 2,
            "one event batch builds two independent cached circuit networks");
        world.Toggle(new(24, 2, 24));
        var remotePower = world.Power.Where(p => p.Key.X >= 24).ToDictionary(p => p.Key, p => p.Value);
        long updates = world.Circuits.TotalNetworkUpdates;
        world.Toggle(new(2, 2, 3));
        check(world.Circuits.LastUpdatedNetworks == 1 && world.Circuits.TotalNetworkUpdates == updates + 1 &&
              world.Circuits.LastVisitedCells > 0 && world.Circuits.LastVisitedCells <= 12 &&
              remotePower.All(p => world.Power.GetValueOrDefault(p.Key) == p.Value),
            "toggling one network updates only that network and preserves remote power");
        check(SamePower(world.Power, ReferencePower(world, out _)), "incremental power matches the independent full BFS oracle");

        updates = world.Circuits.TotalNetworkUpdates;
        world.Set(new(36, 8, 4), Block.Dirt);
        world.Circuits.ProcessPending();
        check(world.Circuits.TotalNetworkUpdates == updates && world.Circuits.LastUpdatedNetworks == 0 && world.Circuits.LastVisitedCells == 0,
            "unrelated non-circuit edits perform no circuit network updates");
        var revisions = world.Chunks.Chunks.ToDictionary(p => p.Key, p => p.Value.Revision);
        world.Circuits.ProcessPending();
        check(revisions.All(p => world.Chunks.Chunks[p.Key].Revision == p.Value),
            "an unchanged circuit tick does not invalidate chunk meshes");
        world.Set(new(3, 2, 3), Block.Air);
        world.Set(new(3, 2, 3), Block.Wire);
        revisions = world.Chunks.Chunks.ToDictionary(p => p.Key, p => p.Value.Revision);
        world.Circuits.ProcessPending();
        check(world.Circuits.LastUpdatedNetworks == 1 && revisions.All(p => world.Chunks.Chunks[p.Key].Revision == p.Value) &&
              SamePower(world.Power, ReferencePower(world, out _)),
            "batched topology edits with unchanged final power do not dirty meshes again during circuit evaluation");

        world.Mine(new(4, 2, 3));
        check(world.Power.GetValueOrDefault(new(7, 2, 3)) == 0 && world.Power.GetValueOrDefault(new(29, 2, 24)) > 0 &&
              SamePower(world.Power, ReferencePower(world, out _)), "removing a wire splits connectivity and leaves independent networks powered");
        world.Place(new(4, 2, 3), Block.Wire);
        check(world.Power.GetValueOrDefault(new(7, 2, 3)) == 10 && SamePower(world.Power, ReferencePower(world, out _)),
            "replacing a wire merges cached networks and restores attenuated signal");

        var terminal = new World(false);
        terminal.Set(new(1, 2, 1), Block.Lever);
        terminal.Set(new(2, 2, 1), Block.Wire);
        terminal.Set(new(3, 2, 1), Block.Lamp);
        terminal.Set(new(4, 2, 1), Block.Wire);
        terminal.Set(new(1, 2, 2), Block.Lever);
        terminal.Set(new(1, 2, 3), Block.Wire);
        terminal.Toggle(new(1, 2, 1));
        check(terminal.Power.GetValueOrDefault(new(3, 2, 1)) == 13 && !terminal.Power.ContainsKey(new(4, 2, 1)) &&
              !terminal.Power.ContainsKey(new(1, 2, 3)) && SamePower(terminal.Power, ReferencePower(terminal, out _)),
            "lamps terminate signals and inactive levers do not relay power");
        terminal.Toggle(new(1, 2, 2));
        check(terminal.Power.GetValueOrDefault(new(1, 2, 3)) == 14 && SamePower(terminal.Power, ReferencePower(terminal, out _)),
            "multiple active sources preserve the strongest signal");

        var fuzz = new World(false);
        for (int x = 1; x < 14; x++) for (int z = 1; z < 14; z++) fuzz.Set(new(x, 1, z), Block.Stone);
        var random = new Random(78115);
        Block[] choices = [Block.Air, Block.Wire, Block.Wire, Block.Lever, Block.Lamp, Block.Stone];
        for (int iteration = 0; iteration < 200; iteration++)
        {
            for (int edit = 0; edit < 4; edit++)
            {
                var cell = new Cell(random.Next(1, 14), 2, random.Next(1, 14));
                if (fuzz.Get(cell) == Block.Lever && random.Next(2) == 0) fuzz.Toggle(cell);
                else fuzz.Set(cell, choices[random.Next(choices.Length)]);
            }
            fuzz.Circuits.ProcessPending();
            if (!SamePower(fuzz.Power, ReferencePower(fuzz, out _)))
                throw new InvalidOperationException($"Differential circuit test failed at seeded batch {iteration}.");
        }
        check(true, "200 seeded edit batches match full BFS after splits, merges, replacements, and source changes");
        var beforeRebuild = new Dictionary<Cell, int>(fuzz.Power);
        fuzz.Circuits.Rebuild();
        check(SamePower(beforeRebuild, fuzz.Power) && fuzz.Circuits.PendingEventCount == 0,
            "rebuilding the connection cache preserves incremental circuit results");
    }

    sealed record Snapshot(byte[] Blocks, int[] Inventory, HashSet<Cell> Switches, Dictionary<Cell, int> Power)
    {
        public static Snapshot Of(World world) => new(world.Blocks, world.Inventory.ToArray(), world.Switches.ToHashSet(), new(world.Power));
        public bool Matches(World world) => Blocks.AsSpan().SequenceEqual(world.Blocks) && Inventory.AsSpan().SequenceEqual(world.Inventory) &&
            Switches.SetEquals(world.Switches) && SamePower(Power, world.Power);
    }

    static void TestHistory(Action<bool, string> check)
    {
        var world = new World(false);
        Cell cell = new(5, 2, 5);
        var before = Snapshot.Of(world);
        check(world.Place(cell, Block.DiamondBlock) && world.UndoCount == 1 && world.RedoCount == 0,
            "successful block placement creates one undo record");
        var placed = Snapshot.Of(world);
        check(world.Undo() && before.Matches(world) && world.UndoCount == 0 && world.RedoCount == 1,
            "undo placement restores blocks and exact inventory");
        check(world.Redo() && placed.Matches(world), "redo placement reapplies blocks and exact inventory debit");
        world.ClearHistory();
        check(world.Mine(cell), "mining a placed block succeeds");
        var mined = Snapshot.Of(world);
        check(world.Undo() && placed.Matches(world) && world.Redo() && mined.Matches(world),
            "mining undo/redo restores blocks and inventory without duplication");
        int undoCount = world.UndoCount;
        check(!world.Mine(cell) && !world.Place(cell, Block.Air) && world.UndoCount == undoCount,
            "failed edits do not enter undo history");

        var support = new World(false);
        AddCircuit(support, 5, 5, 1);
        support.Circuits.ProcessPending();
        support.Toggle(new(5, 2, 5));
        support.ClearHistory();
        var intact = Snapshot.Of(support);
        support.Mine(new(5, 1, 5));
        var broken = Snapshot.Of(support);
        check(support.Get(new(5, 2, 5)) == Block.Air && !support.Switches.Contains(new(5, 2, 5)) && support.UndoCount == 1,
            "support mining and automatic lever reclamation form one history transaction");
        check(support.Undo() && intact.Matches(support) && support.Redo() && broken.Matches(support),
            "undo/redo support removal restores dependent blocks, lever state, circuit power, and inventory");
        support.Undo();
        support.ClearHistory();
        support.Toggle(new(5, 2, 5));
        check(support.Undo() && intact.Matches(support) && support.Redo() && support.Power.Count == 0,
            "lever toggles are undoable without touching inventory");

        var collision = new World(false);
        collision.Set(new(5, 1, 5), Block.Stone);
        collision.Set(new(5, 2, 5), Block.Lever);
        collision.Circuits.ProcessPending();
        collision.Mine(new(5, 1, 5));
        var rejectedState = Snapshot.Of(collision);
        int priorUndo = collision.UndoCount, priorRedo = collision.RedoCount;
        check(!collision.Undo((position, block) => position != new Cell(5, 2, 5)) && rejectedState.Matches(collision) &&
              collision.UndoCount == priorUndo && collision.RedoCount == priorRedo,
            "rejecting a later restored block cancels the entire undo atomically");
        check(collision.Undo(), "a rejected undo stays available for a later valid attempt");
        collision.ClearHistory();
        collision.Place(new(8, 2, 8), Block.Stone);
        collision.Undo();
        rejectedState = Snapshot.Of(collision);
        check(!collision.Redo((_, _) => false) && rejectedState.Matches(collision) && collision.RedoCount == 1,
            "collision rejection leaves redo state and inventory untouched");
        collision.Place(new(9, 2, 8), Block.Dirt);
        check(collision.RedoCount == 0 && !collision.Redo(), "a new edit after undo discards the abandoned redo branch");

        var bounded = new World(false);
        bounded.Set(new(2, 1, 2), Block.Stone);
        bounded.Set(new(2, 2, 2), Block.Lever);
        bounded.Circuits.ProcessPending();
        for (int i = 0; i < 141; i++) bounded.Toggle(new(2, 2, 2));
        check(bounded.UndoCount == 128, "edit history retains at most 128 transactions");
        int undone = 0;
        while (bounded.Undo()) undone++;
        check(undone == 128 && bounded.RedoCount == 128 && bounded.Switches.Contains(new(2, 2, 2)),
            "bounded history discards only the oldest edits and maintains coherent state");
        bounded.ClearHistory();
        check(bounded.UndoCount == 0 && bounded.RedoCount == 0, "explicit history clearing empties both stacks");

        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-history-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            support.Save(path, support.Spawn(), 0, 0);
            support.Place(new(20, 2, 20), Block.Stone);
            support.Load(path);
            check(support.UndoCount == 0 && support.RedoCount == 0 && SamePower(support.Power, ReferencePower(support, out _)),
                "loading a save resets edit history and reconstructs valid circuit caches");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    static void TestClockAndPhysics(Action<bool, string> check)
    {
        var floor = new World(false);
        for (int x = 0; x < World.Size; x++) for (int z = 0; z < World.Size; z++) floor.Set(new(x, 0, z), Block.Stone);
        var results = new List<(Vector3 Position, float Velocity, long Ticks)>();
        foreach (int fps in new[] { 30, 60, 144 })
        {
            var body = new PlayerBody { Position = new(3.5f, 1.01f, 3.5f) };
            var clock = new GameClock(60, 8);
            for (int frame = 0; frame < fps * 2; frame++)
                clock.Advance(1d / fps, dt => body.Step(floor, Vector3.UnitX, 4, false, 0, dt));
            results.Add((body.Position, body.VerticalVelocity, clock.TotalTicks));
        }
        check(results.All(r => r.Ticks == 120) && results.All(r => Vector3.Distance(r.Position, results[0].Position) < .00001f &&
              MathF.Abs(r.Velocity - results[0].Velocity) < .00001f),
            "30/60/144 render FPS produce identical 120-tick movement and gravity results");

        var catchup = new GameClock(60, 8);
        int calls = 0;
        catchup.Advance(1, _ => calls++);
        check(calls == 8 && catchup.StepsThisFrame == 8 && catchup.TotalTicks == 8 && catchup.DroppedSeconds > .8 && catchup.Alpha is >= 0 and < 1,
            "a long render stall runs at most eight simulation ticks and records dropped backlog");
        var paused = new GameClock(60, 8);
        paused.Advance(1d / 120, _ => { });
        paused.ResetAccumulator();
        paused.Advance(1d / 120, _ => calls++);
        check(paused.TotalTicks == 0, "pausing resets leftover simulation time instead of causing a catch-up step");
        long ticks = paused.TotalTicks;
        paused.Advance(0, _ => calls++);
        int rejected = 0;
        foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
            try { paused.Advance(invalid, _ => calls++); }
            catch (ArgumentOutOfRangeException) { rejected++; }
        check(paused.TotalTicks == ticks && rejected == 3, "invalid frame times are rejected and zero time never advances simulation");

        var jumper = new PlayerBody { Position = new(4.5f, 3, 4.5f) };
        for (int i = 0; i < 120; i++) jumper.Step(floor, Vector3.Zero, 4, false, 0, 1f / 60);
        check(jumper.Grounded && jumper.Position.Y >= .99f && jumper.Position.Y <= 1.02f,
            "fixed-step gravity lands on a solid floor without sinking");
        float ground = jumper.Position.Y;
        jumper.Step(floor, Vector3.Zero, 4, true, 0, 1f / 60);
        float jumpVelocity = jumper.VerticalVelocity;
        jumper.Step(floor, Vector3.Zero, 4, true, 0, 1f / 60);
        check(jumper.Position.Y > ground && !jumper.Grounded && jumper.VerticalVelocity > 0 && jumper.VerticalVelocity < jumpVelocity,
            "jumping requires grounded contact and a second airborne jump cannot reset velocity");
        for (int i = 0; i < 120; i++) jumper.Step(floor, Vector3.Zero, 4, false, 0, 1f / 60);
        check(jumper.Grounded && MathF.Abs(jumper.Position.Y - ground) < .02f, "a complete jump returns to the same ground height");

        for (int y = 1; y <= 3; y++) for (int z = 3; z <= 5; z++) floor.Set(new(8, y, z), Block.Stone);
        var walker = new PlayerBody { Position = new(6.5f, 1.01f, 4.5f) };
        for (int i = 0; i < 60; i++) walker.Step(floor, Vector3.UnitX, 4, false, 0, 1f / 60);
        check(walker.Position.X < 8 && walker.Position.X > 7 && !CollisionSystem.Collides(floor, walker.Position),
            "horizontal movement stops at walls without overlap");
        var fast = new PlayerBody { Position = new(6.5f, 1.01f, 4.5f) };
        fast.Step(floor, Vector3.UnitX, 100, false, 0, .1f);
        check(fast.Position.X < 8 && !CollisionSystem.Collides(floor, fast.Position),
            "swept collision prevents a fast movement step from tunneling through a one-block wall");
        check(CollisionSystem.OverlapsPlayer(new(6, 1, 4), new(6.5f, 1, 4.5f)) &&
              !CollisionSystem.OverlapsPlayer(new(8, 1, 4), new(6.5f, 1, 4.5f)),
            "player overlap checks distinguish blocked and safe block restoration positions");
        walker.Flying = true;
        float initialY = walker.Position.Y;
        for (int i = 0; i < 30; i++) walker.Step(floor, Vector3.Zero, 4, false, 1, 1f / 60);
        check(walker.Position.Y > initialY + 1 && walker.VerticalVelocity == 0, "flight ascends while suppressing gravity velocity");
        walker.Flying = false;
        float releaseHeight = walker.Position.Y;
        for (int i = 0; i < 10; i++) walker.Step(floor, Vector3.Zero, 4, false, 0, 1f / 60);
        check(walker.Position.Y < releaseHeight && walker.VerticalVelocity < 0, "leaving flight restores fixed-step gravity");
    }

    static void TestSettings(Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var missing = GameSettings.Load(path);
            check(missing.TargetFps == 60 && missing.FieldOfView == 72 && missing.LoadWarning is null,
                "a missing settings file uses defaults without an error warning");
            File.WriteAllText(path, "{broken");
            var malformed = GameSettings.Load(path);
            check(malformed.TargetFps == 60 && malformed.LoadWarning is not null,
                "malformed settings recover to defaults with a readable warning");
            File.WriteAllText(path, "{\"TargetFps\":13,\"FieldOfView\":200,\"MouseSensitivity\":-1,\"DrawDistance\":999,\"SelectedSlot\":20,\"Hotbar\":[0,9,255,3,4,5,6,7]}");
            var invalid = GameSettings.Load(path);
            var palette = new BlockPaletteModel();
            invalid.ApplyToPalette(palette);
            check(invalid.TargetFps == 60 && invalid.FieldOfView == 100 && invalid.MouseSensitivity == .001f &&
                  invalid.DrawDistance == 128 && palette.SelectedSlot == 7 && palette.Hotbar[0] == Block.Grass &&
                  palette.Hotbar[1] == Block.Dirt && palette.Hotbar[2] == Block.Stone,
                "settings clamp controls and replace non-placeable or invalid hotbar slots");
            File.WriteAllText(path, "{\"Hotbar\":[3,4]}");
            invalid = GameSettings.Load(path);
            check(invalid.Hotbar.Length == 8 && invalid.Hotbar.SequenceEqual(new BlockPaletteModel().Hotbar),
                "a hotbar with the wrong length falls back to all eight default slots");

            var settings = new GameSettings { TargetFps = 144, FieldOfView = 86, MouseSensitivity = .004f, DrawDistance = 96 };
            Block[] hotbar = [Block.CherryLog, Block.BlueGlass, Block.DiamondBlock, Block.RedWool, Block.StoneBricks, Block.Wire, Block.Lever, Block.Lamp];
            hotbar.CopyTo(palette.Hotbar, 0);
            palette.SelectedSlot = 4;
            settings.CapturePalette(palette);
            settings.Save(path);
            var restored = GameSettings.Load(path);
            var restoredPalette = new BlockPaletteModel();
            restored.ApplyToPalette(restoredPalette);
            check(restored.TargetFps == 144 && restored.FieldOfView == 86 && restored.MouseSensitivity == .004f && restored.DrawDistance == 96 &&
                  restoredPalette.SelectedSlot == 4 && restoredPalette.Hotbar.SequenceEqual(hotbar),
                "graphics controls and all eight custom hotbar slots survive settings save/load");
            restoredPalette.Hotbar[0] = Block.Stone;
            check(restored.Hotbar[0] == Block.CherryLog, "applying settings does not share mutable hotbar storage with the live palette");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
