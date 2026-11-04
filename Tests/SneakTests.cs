using System.Numerics;

namespace BlockCircuit;

public static class SneakTests
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

        var platform = new World(false);
        platform.Set(new(10, 0, 10), Block.Stone);
        var body = new PlayerBody { Position = new(10.5f, 1, 10.5f) };
        body.Step(platform, Vector3.Zero, 4, false, 0, 1f / 60, sneak: true);
        Check(body.Crouching && body.BodyHeight == 1.5f && body.EyeHeight < 1.4f && body.Grounded,
            "Shift lowers the collision body and camera while grounded");
        body.Step(platform, Vector3.Zero, 4, false, 0, 1f / 60);
        Check(!body.Crouching && body.BodyHeight == CollisionSystem.BodyHeight && body.EyeHeight == 1.62f,
            "releasing Shift stands up when there is headroom");

        var roof = new World(false);
        roof.Set(new(10, 0, 10), Block.Stone);
        roof.Set(new(10, 3, 10), Block.Stone);
        body.Position = new(10.5f, 1.4f, 10.5f);
        body.VerticalVelocity = 0;
        body.Step(roof, Vector3.Zero, 0, false, 0, .001f, sneak: true);
        body.Step(roof, Vector3.Zero, 0, false, 0, .001f);
        Check(body.Crouching && !CollisionSystem.Collides(roof, body.Position, body.BodyHeight),
            "releasing Shift cannot expand the player into a low ceiling");
        Check(!CollisionSystem.OverlapsPlayer(new(10, 3, 10), body.Position, body.BodyHeight) &&
            CollisionSystem.OverlapsPlayer(new(10, 3, 10), body.Position),
            "placement overlap checks respect the current crouched height");
        roof.Set(new(10, 3, 10), Block.Air);
        body.Step(roof, Vector3.Zero, 0, false, 0, .001f);
        Check(!body.Crouching, "standing resumes once the low ceiling is removed");

        var tunnel = new World(false);
        tunnel.Set(new(11, 3, 10), Block.Stone);
        var standing = new Vector3(10.5f, 1.4f, 10.5f);
        var crouched = standing;
        CollisionSystem.Move(tunnel, ref standing, new Vector3(2, 0, 0));
        CollisionSystem.Move(tunnel, ref crouched, new Vector3(2, 0, 0), PlayerBody.CrouchHeight);
        Check(standing.X < 11 && Math.Abs(crouched.X - 12.5f) < .001f,
            "swept horizontal collision uses the lower crouched body");
        crouched = new(11.5f, 1.4f, 10.5f);
        CollisionSystem.Move(tunnel, ref crouched, new Vector3(0, 2, 0), PlayerBody.CrouchHeight);
        Check(Math.Abs(crouched.Y - 1.5f) < .001f,
            "upward movement stops the crouched head at the ceiling");

        body = new PlayerBody { Position = new(10.5f, 1, 10.5f) };
        for (int i = 0; i < 120; i++) body.Step(platform, Vector3.UnitX, 4, false, 0, 1f / 60, sneak: true);
        Check(body.Position.X > 10.9f && body.Position.X < 11.29f && body.Grounded &&
            CollisionSystem.HasSupport(platform, body.Position), "sneaking approaches an edge without falling");

        body = new PlayerBody { Position = new(10.5f, 1, 10.5f) };
        for (int i = 0; i < 120; i++) body.Step(platform, new(1, 0, 1), 4, false, 0, 1f / 60, sneak: true);
        Check(body.Position.X > 10.9f && body.Position.Z > 10.9f && body.Grounded &&
            CollisionSystem.HasSupport(platform, body.Position), "diagonal sneaking cannot slip off a platform corner");

        var gap = new World(false);
        gap.Set(new(10, 0, 10), Block.Stone);
        gap.Set(new(13, 0, 10), Block.Stone);
        body = new PlayerBody { Position = new(10.5f, 1, 10.5f) };
        body.Step(gap, Vector3.UnitX, 16, false, 0, .5f, sneak: true);
        Check(body.Position.X < 11.29f && body.Grounded,
            "a long sneak step cannot skip an unsupported gap to another platform");

        body = new PlayerBody { Position = new(10.95f, 1, 10.5f) };
        body.Step(platform, Vector3.UnitX, 4, true, 0, .1f, sneak: true);
        Check(body.Position.X > 11.29f && body.Position.Y > 1 && !body.Grounded,
            "jumping deliberately leaves a ledge even while holding Shift");
        body = new PlayerBody { Position = new(10.95f, 1, 10.5f) };
        body.Step(platform, Vector3.UnitX, 4, false, 0, .1f);
        Check(body.Position.X > 11.29f && body.Position.Y < 1,
            "normal walking can still walk off an edge");

        var wall = new World(false);
        wall.Set(new(10, 1, 10), Block.Stone);
        Check(!CollisionSystem.HasSupport(wall, new(9.75f, 1, 10.5f)),
            "a nearby wall does not count as ground support");

        body = new PlayerBody { Position = new(10.5f, 6, 10.5f), Flying = true };
        body.Step(platform, Vector3.Zero, 4, false, -1, .1f, sneak: true);
        Check(!body.Crouching && Math.Abs(body.Position.Y - 5.6f) < .001f && !body.Grounded,
            "Shift descends in flight without switching to a crouched body");
        body.Flying = false;
        body.Step(platform, Vector3.Zero, 4, false, 0, .1f, sneak: true);
        Check(body.Crouching && body.Position.Y < 5.6f,
            "sneaking in the air does not suspend gravity");

        Console.WriteLine($"Sneak tests: {passed} passed.");
    }
}
