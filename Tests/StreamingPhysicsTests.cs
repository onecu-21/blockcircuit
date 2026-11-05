using System.Numerics;

namespace BlockCircuit;

/// <summary>Streaming player/camera movement and unloaded-frontier safety without a graphics context.</summary>
static class StreamingPhysicsTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }

        var floor = Floor(-48, 96, -16, 15);
        var negative = new PlayerBody { Position = new(-3.5f, 1, 3.5f) };
        for (int tick = 0; tick < 60; tick++)
            negative.Step(floor, -Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, .05f);
        Check(negative.Position.X < -15 && negative.Grounded && !CollisionSystem.Collides(floor, negative.Position),
            "streaming players walk through negative coordinates without the old zero-coordinate wall");

        var positive = new PlayerBody { Position = new(38.5f, 1, 3.5f) };
        for (int tick = 0; tick < 30; tick++)
            positive.Step(floor, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, .05f);
        Check(positive.Position.X > 43 && positive.Grounded && !CollisionSystem.Collides(floor, positive.Position),
            "streaming players cross the old 40-block world edge while retaining floor collision");

        for (int y = 1; y <= 3; y++) for (int z = 2; z <= 4; z++) floor.Set(new(-8, y, z), Block.Stone);
        var wall = new PlayerBody { Position = new(-10.5f, 1, 3.5f) };
        for (int tick = 0; tick < 30; tick++) wall.Step(floor, Vector3.UnitX, PhysicsProfile.SprintSpeed, false, 0, .05f);
        Check(wall.Position.X <= -8 - CollisionSystem.HalfWidth + .0001f && wall.HorizontalVelocity.X == 0 &&
              !CollisionSystem.Collides(floor, wall.Position), "swept collision stops a sprint at a negative-coordinate wall");

        var frontier = Floor(0, 15, 0, 15);
        var waiting = new PlayerBody { Position = new(14.5f, 1, 3.5f) };
        for (int tick = 0; tick < 60; tick++) waiting.Step(frontier, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, .05f);
        Check(waiting.Position.X <= 16 - CollisionSystem.HalfWidth + .0001f && waiting.Position.X > 15 &&
              waiting.HorizontalVelocity.X == 0 && !CollisionSystem.Collides(frontier, waiting.Position),
            "unloaded neighboring chunks act as collision barriers rather than temporary empty space");
        for (int x = 16; x <= 31; x++) for (int z = 0; z < 16; z++) frontier.Set(new(x, 0, z), Block.Stone);
        for (int tick = 0; tick < 8; tick++) waiting.Step(frontier, Vector3.UnitX, PhysicsProfile.WalkSpeed, false, 0, .05f);
        Check(waiting.Position.X > 16 && waiting.Grounded,
            "movement resumes through a former loading frontier after its neighbor becomes available");

        var missingBelow = new World(false);
        missingBelow.Set(new(3, 20, 3), Block.Stone);
        missingBelow.EnableStreaming();
        var faller = new PlayerBody { Position = new(4.5f, 17, 4.5f) };
        for (int tick = 0; tick < 30; tick++) faller.Step(missingBelow, Vector3.Zero, 0, false, 0, .05f);
        Check(faller.Position.Y >= 16 && faller.Grounded,
            "a player cannot fall into an unavailable lower chunk while it is loading");

        var high = new World(false);
        high.Set(new(1, 32, 1), Block.Stone);
        high.EnableStreaming();
        var flyer = new PlayerBody { Position = new(3.5f, 40, 3.5f), Flying = true };
        for (int tick = 0; tick < 10; tick++) flyer.Step(high, Vector3.Zero, 4, false, 1, .05f);
        Check(flyer.Position.Y > 41.9f && flyer.VerticalVelocity == 0,
            "streaming flight can rise above the former finite-world ceiling");
        Check(CollisionSystem.MaximumFeetHeight(high) == 255 && CollisionSystem.MaximumFeetHeight(new World(false)) == World.Height + 8,
            "streaming and legacy finite worlds retain their separate flight height limits");

        var ledge = new World(false);
        ledge.Set(new(-10, 0, -10), Block.Stone);
        ledge.EnableStreaming();
        var sneak = new PlayerBody { Position = new(-9.5f, 1, -9.5f) };
        for (int tick = 0; tick < 80; tick++)
            sneak.Step(ledge, new(1, 0, 1), PhysicsProfile.SneakSpeed, false, 0, .05f, true);
        Check(sneak.Grounded && CollisionSystem.HasSupport(ledge, sneak.Position) &&
              sneak.Position.X < -8.7f && sneak.Position.Z < -8.7f,
            "negative-coordinate diagonal sneaking preserves support at a platform corner");

        var cameraFloor = Floor(-32, 79, 0, 15);
        var positiveEye = new Vector3(48.5f, 2.62f, 3.5f);
        var negativeEye = new Vector3(-8.5f, 2.62f, 3.5f);
        Check(Vector3.Distance(CameraRig.ResolvePosition(cameraFloor, positiveEye, Vector3.UnitX), positiveEye + new Vector3(4, 0, 0)) < .0001f &&
              Vector3.Distance(CameraRig.ResolvePosition(cameraFloor, negativeEye, -Vector3.UnitX), negativeEye - new Vector3(4, 0, 0)) < .0001f,
            "third-person cameras use their full distance in loaded space beyond both old horizontal boundaries");
        var cameraFrontier = Floor(0, 15, 0, 15);
        var frontierCamera = CameraRig.ResolvePosition(cameraFrontier, new(14.5f, 2.62f, 3.5f), Vector3.UnitX);
        Check(frontierCamera.X < 16 - CameraRig.Radius && frontierCamera.X > 15.7f,
            "the camera lens stops before an unavailable chunk frontier");
        cameraFloor.Set(new(-5, 2, 3), Block.Glass);
        var glassCamera = CameraRig.ResolvePosition(cameraFloor, negativeEye, Vector3.UnitX);
        Check(glassCamera.X < -5 - CameraRig.Radius && glassCamera.X > -5.4f,
            "solid transparent blocks obstruct the camera at negative coordinates");

        var veryFast = new Vector3(1.5f, 1, 1.5f);
        var hit = CollisionSystem.Move(cameraFrontier, ref veryFast, new(float.MaxValue, 0, 0));
        Check(hit.HitX && float.IsFinite(veryFast.X) && veryFast.X < 16,
            "enormous streaming tool movements remain bounded and cannot skip unloaded chunks");
        var outside = new Vector3(float.MaxValue, float.MaxValue, float.MinValue);
        CollisionSystem.Move(cameraFrontier, ref outside, new(float.MaxValue, -float.MaxValue, float.MinValue));
        Check(Math.Abs(outside.X) <= World.MaximumPlayerCoordinate && Math.Abs(outside.Z) <= World.MaximumPlayerCoordinate &&
              outside.Y <= CollisionSystem.StreamingMaximumFeetY && float.IsFinite(outside.Y),
            "enormous finite streaming origins are clamped before voxel range conversion");

        var frameWorld = Floor(-32, 47, 0, 15);
        var trajectories = new List<(Vector3 Position, float Vertical, Vector3 Horizontal, bool Grounded)[]>();
        foreach (int fps in new[] { 60, 144, 240 })
        {
            var body = new PlayerBody { Position = new(-3.5f, 1, 3.5f) };
            var clock = new GameClock();
            var ticks = new List<(Vector3, float, Vector3, bool)>();
            int tick = 0;
            for (int frame = 0; frame < fps * 4; frame++)
                clock.Advance(1d / fps, dt =>
                {
                    body.Step(frameWorld, Vector3.UnitX, PhysicsProfile.WalkSpeed, tick++ == 20, 0, dt);
                    ticks.Add((body.Position, body.VerticalVelocity, body.HorizontalVelocity, body.Grounded));
                });
            trajectories.Add(ticks.ToArray());
        }
        Check(trajectories.All(t => t.Length == 80 && t.SequenceEqual(trajectories[0])) && trajectories[0][79].Position.X > 0,
            "60/144/240 render FPS produce the same full streaming movement trajectory across zero coordinates");

        var circuit = new World(false);
        Cell support = new(-40, 1, 8), wire = new(-40, 2, 8);
        circuit.Set(support, Block.Stone); circuit.EnableStreaming();
        int stock = circuit.Inventory[(int)Block.Wire];
        Check(circuit.Place(wire, Block.Wire) && circuit.Mine(support) && circuit.Get(wire) == Block.Air &&
              circuit.Inventory[(int)Block.Wire] == stock,
            "support removal beyond the legacy bounds recovers unsupported circuit parts exactly once");
        Console.WriteLine($"Streaming physics tests passed: {passed}");
    }

    static World Floor(int minimumX, int maximumX, int minimumZ, int maximumZ)
    {
        var world = new World(false);
        for (int x = minimumX; x <= maximumX; x++)
        for (int z = minimumZ; z <= maximumZ; z++) world.Set(new(x, 0, z), Block.Stone);
        world.EnableStreaming();
        return world;
    }
}
