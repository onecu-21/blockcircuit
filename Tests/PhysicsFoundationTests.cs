using System.Numerics;

namespace BlockCircuit;

/// <summary>Behavioral acceptance checks for the 20 TPS simulation contract.</summary>
static class PhysicsFoundationTests
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
        TestClock(Check);
        TestInput(Check);
        TestMovement(Check);
        string settingsPath = Path.Combine(Path.GetTempPath(), "blockcircuit-240fps-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new GameSettings { TargetFps = 240 };
            settings.Save(settingsPath);
            Check(settings.TargetFps == 240 && GameSettings.Load(settingsPath).TargetFps == 240,
                "the 240 FPS render target is accepted and survives settings save/load");
        }
        finally { File.Delete(settingsPath); File.Delete(settingsPath + ".tmp"); }
        Console.WriteLine($"Physics foundation tests passed: {count}");
    }

    static void TestClock(Action<bool, string> check)
    {
        var clock = new GameClock();
        check(GameClock.DefaultTickRate == 20 && Math.Abs(clock.StepSeconds - .05f) < 1e-8,
            "the default simulation clock advances in 50 ms steps at 20 TPS");
        int calls = 0;
        clock.Advance(.049, _ => calls++);
        check(calls == 0 && clock.Alpha > .97f && clock.Alpha < 1,
            "a frame before the 50 ms boundary interpolates without running an early tick");
        clock.Advance(.001, dt => { check(dt == .05f, "simulation callbacks receive a fixed 50 ms timestep"); calls++; });
        check(calls == 1 && clock.TotalTicks == 1 && clock.Alpha == 0,
            "crossing exactly 50 ms runs one simulation tick and leaves no backlog");
        clock.Advance(.025, _ => calls++);
        check(clock.Alpha > .4999f && clock.Alpha < .5001f && calls == 1,
            "interpolation advances midway through a tick while simulation remains unchanged");
        clock.ResetAccumulator();
        check(clock.Alpha == 0 && clock.TotalTicks == 1 && clock.StepsThisFrame == 0,
            "pausing clears interpolation backlog while preserving completed tick counts");

        foreach (int fps in new[] { 30, 60, 144, 240 })
        {
            var partitioned = new GameClock();
            for (int frame = 0; frame < fps * 30; frame++) partitioned.Advance(1d / fps, _ => { });
            check(partitioned.TotalTicks == 600 && partitioned.DroppedSeconds == 0 && partitioned.Alpha < 1e-6,
                $"{fps} render FPS produces exactly 600 ticks over 30 seconds without drift");
        }
        var irregular = new GameClock();
        double total = 0;
        double[] durations = [.001, .006, .027, .013, .023, .005];
        for (int frame = 0; frame < 2400; frame++)
        {
            double duration = durations[frame % durations.Length];
            total += duration;
            irregular.Advance(duration, _ => { });
        }
        check(irregular.TotalTicks == (long)Math.Floor(total / .05 + 1e-7) && irregular.TotalTicks == 600 && irregular.DroppedSeconds == 0,
            "irregular render partitions retain all 600 ticks over 30 seconds");

        var bounded = new GameClock(20, 2);
        bounded.Advance(1, _ => { });
        check(bounded.TotalTicks == 2 && bounded.StepsThisFrame == 2 && Math.Abs(bounded.DroppedSeconds - .9) < 1e-8 && bounded.Alpha == 0,
            "a one-second stall obeys a two-tick catch-up budget and accounts for 900 ms dropped time");
        long before = bounded.TotalTicks;
        int rejected = 0;
        foreach (double invalid in new[] { -.01, double.NaN, double.PositiveInfinity })
            try { bounded.Advance(invalid, _ => { }); }
            catch (ArgumentOutOfRangeException) { rejected++; }
        bounded.Advance(0, _ => { });
        check(rejected == 3 && bounded.TotalTicks == before,
            "invalid wall times are rejected and a zero-length render frame creates no ticks");
    }

    static void TestInput(Action<bool, string> check)
    {
        var input = new SimulationInput();
        input.Sample(Vector3.UnitX, PhysicsProfile.WalkSpeed, true, true, 0, false);
        input.Sample(Vector3.UnitZ, PhysicsProfile.WalkSpeed, false, false, 0, true);
        var tapped = input.Consume();
        check(tapped.Jump && tapped.Direction == Vector3.UnitZ && tapped.Sneak,
            "a jump pressed and released between ticks survives while the latest movement state is consumed");
        var next = input.Consume();
        check(!next.Jump && next.Direction == Vector3.UnitZ && next.Sneak,
            "a buffered jump is consumed once while held movement persists across catch-up ticks");
        input.Sample(Vector3.Zero, PhysicsProfile.WalkSpeed, false, true, 1, false);
        check(input.Consume().Jump && input.Consume().Jump,
            "holding Space remains active across successive simulation ticks");
        input.Sample(Vector3.UnitX, PhysicsProfile.SprintSpeed, true, true, 1, true);
        input.Reset();
        var reset = input.Consume();
        check(!reset.Jump && reset.Direction == Vector3.Zero && reset.FlyVertical == 0 && !reset.Sneak,
            "opening a menu clears buffered jump, movement, flight, and sneak input");

        var floor = Floor();
        foreach (int fps in new[] { 60, 144, 240 })
        {
            var frameInput = new SimulationInput();
            var clock = new GameClock();
            var body = new PlayerBody { Position = new(3.5f, 1, 3.5f) };
            int jumpTicks = 0;
            float firstTickHeight = 0;
            for (int frame = 0; frame < fps; frame++)
            {
                // Both the press and release occur before the first 50 ms tick.
                frameInput.Sample(Vector3.UnitX, PhysicsProfile.WalkSpeed, frame == 0, frame == 0, 0, false);
                clock.Advance(1d / fps, step =>
                {
                    var movement = frameInput.Consume();
                    if (movement.Jump) jumpTicks++;
                    body.Step(floor, movement.Direction, movement.Speed, movement.Jump, movement.FlyVertical, step, movement.Sneak);
                    if (clock.TotalTicks == 0) firstTickHeight = body.Position.Y;
                });
            }
            check(clock.TotalTicks == 20 && jumpTicks == 1 && Math.Abs(firstTickHeight - 1.42f) < .00002,
                $"a short jump press released between {fps} FPS frames reaches exactly the first 20 TPS movement tick");
        }

        var flightInput = new SimulationInput();
        var flightClock = new GameClock();
        var flyer = new PlayerBody { Position = new(20.5f, 5, 20.5f) };
        flightInput.QueueFlightToggle();
        flightClock.Advance(.049, _ =>
        {
            if (flightInput.Consume().ToggleFlight) flyer.Flying = !flyer.Flying;
        });
        check(!flyer.Flying && flightInput.FlightTogglePending && flightClock.TotalTicks == 0,
            "a queued flight toggle remains pending and does not change physics before the next tick");
        flightClock.Advance(.001, _ =>
        {
            if (flightInput.Consume().ToggleFlight) flyer.Flying = !flyer.Flying;
        });
        check(flyer.Flying && !flightInput.FlightTogglePending && !flightInput.Consume().ToggleFlight,
            "a fixed simulation tick applies a queued flight toggle exactly once");
        flightInput.QueueFlightToggle();
        flightInput.QueueFlightToggle();
        check(!flightInput.FlightTogglePending && !flightInput.Consume().ToggleFlight,
            "two flight toggles captured before the same tick cancel by parity");
        flightInput.QueueFlightToggle();
        flightInput.Reset();
        check(!flightInput.FlightTogglePending && !flightInput.Consume().ToggleFlight,
            "opening a menu clears a flight toggle queued before the next simulation tick");
    }

    static World Floor()
    {
        var floor = new World(false);
        for (int x = 0; x < World.Size; x++)
        for (int z = 0; z < World.Size; z++) floor.Set(new(x, 0, z), Block.Stone);
        floor.Circuits.ProcessPending();
        return floor;
    }

    static void TestMovement(Action<bool, string> check)
    {
        const float dt = .05f;
        var floor = Floor();
        var jumper = new PlayerBody { Position = new(5.5f, 1, 5.5f) };
        // Independent jump trajectory in blocks, for move-then-gravity 20 Hz integration.
        double[] expectedRise = [.42, .7532, 1.001336, 1.16610928, 1.2491870944, 1.252203352512];
        bool trajectory = true;
        for (int tick = 0; tick < expectedRise.Length; tick++)
        {
            jumper.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, tick == 0, 0, dt);
            trajectory &= Math.Abs(jumper.Position.Y - 1 - expectedRise[tick]) < .00002;
        }
        check(trajectory && jumper.VerticalVelocity < 0,
            "a 20 TPS jump follows the independent trajectory and reaches an approximately 1.2522-block apex");
        for (int tick = 0; tick < 30; tick++) jumper.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, false, 0, dt);
        check(jumper.Grounded && Math.Abs(jumper.Position.Y - 1) < .00001 && !CollisionSystem.Collides(floor, jumper.Position),
            "a complete 20 TPS jump lands exactly on its original floor without penetration");

        var falling = new PlayerBody { Position = new(6.5f, 12, 6.5f) };
        falling.Step(floor, Vector3.Zero, 0, false, 0, dt);
        falling.Step(floor, Vector3.Zero, 0, false, 0, dt);
        check(Math.Abs(falling.Position.Y - 11.9216f) < .00002 && Math.Abs(falling.VerticalVelocity - -3.10464f) < .00002,
            "free fall applies 0.08 blocks per tick gravity and vertical drag after movement");

        var walker = new PlayerBody { Position = new(3.5f, 1, 3.5f) };
        for (int tick = 0; tick < 100; tick++) walker.Step(floor, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, dt);
        float start = walker.Position.X;
        walker.Step(floor, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, dt);
        check(Math.Abs((walker.Position.X - start) / dt - 4.31718f) < .0001,
            "ordinary ground walking converges to approximately 4.31718 blocks per second");
        float speedBeforeRelease = walker.HorizontalVelocity.X;
        start = walker.Position.X;
        walker.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, false, 0, dt);
        check(walker.Position.X > start && walker.HorizontalVelocity.X > 0 && walker.HorizontalVelocity.X < speedBeforeRelease * .55f,
            "releasing movement preserves momentum briefly and ordinary ground friction slows it");
        for (int tick = 0; tick < 30; tick++) walker.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, false, 0, dt);
        check(walker.HorizontalVelocity.Length() < .0001f,
            "ordinary ground friction brings released movement to rest");

        var diagonal = new PlayerBody { Position = new(3.5f, 1, 3.5f) };
        var straight = new PlayerBody { Position = diagonal.Position };
        for (int tick = 0; tick < 40; tick++)
        {
            diagonal.Step(floor, new(1, 0, 1), PhysicsProfile.WalkSpeed, false, 0, dt);
            straight.Step(floor, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, dt);
        }
        check(Math.Abs(Vector3.Distance(diagonal.Position, new(3.5f, 1, 3.5f)) - (straight.Position.X - 3.5f)) < .0001,
            "diagonal walking cannot exceed straight-line ground movement speed");

        bool speedProfiles = true;
        foreach (var profile in new[] { (Speed: PhysicsProfile.SprintSpeed, Sneak: false, Expected: 5.6123348f),
                     (Speed: PhysicsProfile.SneakSpeed, Sneak: true, Expected: 1.2951542f) })
        {
            var body = new PlayerBody { Position = new(3.5f, 1, 15.5f) };
            for (int tick = 0; tick < 80; tick++) body.Step(floor, Vector3.UnitX, profile.Speed, false, 0, dt, profile.Sneak);
            float previousX = body.Position.X;
            body.Step(floor, Vector3.UnitX, profile.Speed, false, 0, dt, profile.Sneak);
            speedProfiles &= Math.Abs((body.Position.X - previousX) / dt - profile.Expected) < .0001;
        }
        check(speedProfiles, "sprinting and sneaking converge to approximately 5.61233 and 1.29515 blocks per second");
        var airborne = new PlayerBody { Position = new(3.5f, 1, 18.5f) };
        for (int tick = 0; tick < 20; tick++) airborne.Step(floor, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, dt);
        airborne.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, true, 0, dt);
        float carriedSpeed = airborne.HorizontalVelocity.X;
        airborne.Step(floor, Vector3.Zero, PhysicsProfile.WalkSpeed, false, 0, dt);
        check(!airborne.Grounded && Math.Abs(airborne.HorizontalVelocity.X - carriedSpeed * .91f) < .00001,
            "airborne movement preserves horizontal momentum with 0.91 drag per fixed tick");
        var ledge = new World(false);
        ledge.Set(new(10, 0, 10), Block.Stone);
        var sneaking = new PlayerBody { Position = new(10.5f, 1, 10.5f) };
        for (int tick = 0; tick < 80; tick++) sneaking.Step(ledge, new(1, 0, 1), PhysicsProfile.SneakSpeed, false, 0, dt, sneak: true);
        check(sneaking.Grounded && CollisionSystem.HasSupport(ledge, sneaking.Position) && sneaking.Position.Y == 1 &&
              sneaking.Position.X < 11.3f && sneaking.Position.Z < 11.3f,
            "20 TPS diagonal sneaking remains supported at the corner of a one-block ledge");

        for (int z = 0; z < World.Size; z++)
        for (int y = 1; y <= 5; y++) floor.Set(new(12, y, z), Block.Stone);
        var wallRunner = new PlayerBody { Position = new(10.5f, 1, 8.5f) };
        for (int tick = 0; tick < 30; tick++) wallRunner.Step(floor, Vector3.UnitX, PhysicsProfile.SprintSpeed, false, 0, dt);
        check(wallRunner.Position.X < 12 && wallRunner.HorizontalVelocity.X == 0 && !CollisionSystem.Collides(floor, wallRunner.Position),
            "sprinting into a wall clips position and clears blocked-axis momentum");
        floor.Set(new(6, 3, 6), Block.Stone);
        var underRoof = new PlayerBody { Position = new(6.5f, 1, 6.5f) };
        underRoof.Step(floor, Vector3.Zero, 0, true, 0, dt);
        check(underRoof.Position.Y <= 3 - underRoof.BodyHeight + .00001f && underRoof.VerticalVelocity <= 0 &&
              !CollisionSystem.Collides(floor, underRoof.Position),
            "a jump under a low ceiling stops the head without penetrating the roof");

        var traces = new List<List<(Vector3 Position, float Velocity, Vector3 Horizontal, bool Grounded)>>();
        foreach (int fps in new[] { 30, 60, 144, 240 })
        {
            var body = new PlayerBody { Position = new(3.5f, 1, 8.5f) };
            var clock = new GameClock();
            var trace = new List<(Vector3 Position, float Velocity, Vector3 Horizontal, bool Grounded)>();
            int tick = 0;
            for (int frame = 0; frame < fps * 4; frame++)
                clock.Advance(1d / fps, step =>
                {
                    body.Step(floor, tick < 60 ? Vector3.UnitX : Vector3.Zero, PhysicsProfile.WalkSpeed, tick == 20, 0, step);
                    trace.Add((body.Position, body.VerticalVelocity, body.HorizontalVelocity, body.Grounded));
                    tick++;
                });
            traces.Add(trace);
        }
        check(traces.All(trace => trace.Count == 80 && trace.SequenceEqual(traces[0])),
            "30/60/144/240 render FPS produce identical positions, velocities, and ground contact at every walking, jump, collision, and release tick");

        var flyer = new PlayerBody { Position = new(20.5f, 5, 20.5f), Flying = true };
        flyer.Step(floor, Vector3.UnitX, 4, false, 1, dt);
        check(Math.Abs(flyer.Position.X - 20.7f) < .00001 && Math.Abs(flyer.Position.Y - 5.2f) < .00001 && flyer.VerticalVelocity == 0,
            "flight moves at its chosen speed on each axis without applying gravity");
        flyer.Flying = false;
        flyer.Step(floor, Vector3.Zero, 4, false, 0, dt);
        flyer.Step(floor, Vector3.Zero, 4, false, 0, dt);
        check(flyer.Position.Y < 5.2f && flyer.VerticalVelocity < 0,
            "leaving flight restores gravity within two fixed ticks");
        flyer.Position = new(22.5f, 6, 22.5f);
        check(flyer.VerticalVelocity == 0 && flyer.HorizontalVelocity == Vector3.Zero,
            "teleporting clears old movement momentum before a new simulation step");
    }
}
