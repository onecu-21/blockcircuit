using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>CPU fixed-tick cost and managed allocation; does not measure rendering.</summary>
static class PhysicsBenchmark
{
    public static void Run()
    {
        var world = new World(false);
        for (int x = 0; x < World.Size; x++)
        for (int z = 0; z < World.Size; z++) world.Set(new(x, 0, z), Block.Stone);
        for (int y = 1; y < 6; y++)
        for (int z = 0; z < World.Size; z++) world.Set(new(18, y, z), Block.Stone);
        world.Circuits.ProcessPending();
        const int warmupTicks = 10000, measuredTicks = 100000;
        var body = new PlayerBody();
        var durations = new double[measuredTicks];
        for (int tick = 0; tick < warmupTicks; tick++) Step(body, world, tick);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int tick = 0; tick < measuredTicks; tick++)
        {
            long tickStart = Stopwatch.GetTimestamp();
            Step(body, world, tick);
            durations[tick] = Stopwatch.GetElapsedTime(tickStart).TotalMicroseconds;
        }
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Array.Sort(durations);
        var report = new
        {
            SchemaVersion = 1,
            Scope = "CPU only: one player walking, sprinting into a wall, jumping and sneaking at 20 TPS. No frame/FPS/GPU or long-run residency claim.",
            TimestampUtc = DateTime.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            TickRate = PhysicsProfile.TickRate,
            WarmupTicks = warmupTicks,
            MeasuredTicks = measuredTicks,
            TotalMilliseconds = milliseconds,
            ManagedAllocatedBytes = allocated,
            MedianTickMicroseconds = durations[measuredTicks / 2],
            P95TickMicroseconds = durations[(int)(measuredTicks * .95) - 1],
            P99TickMicroseconds = durations[(int)(measuredTicks * .99) - 1]
        };
        File.WriteAllText("physics-benchmark-results.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"20 TPS physics: {measuredTicks} ticks in {milliseconds:F3} ms; {allocated} B managed allocation; P99 {report.P99TickMicroseconds:F3} us (CPU only)");
    }

    static void Step(PlayerBody body, World world, int tick)
    {
        if (tick % 200 == 0) body.Position = new(3.5f, 1, 10.5f);
        int phase = tick % 200;
        bool sneak = phase >= 160;
        body.Step(world, phase < 140 ? Vector3.UnitX : Vector3.Zero,
            sneak ? PhysicsProfile.SneakSpeed : phase < 80 ? PhysicsProfile.WalkSpeed : PhysicsProfile.SprintSpeed,
            phase == 30 || phase == 110, 0, PhysicsProfile.TickSeconds, sneak);
        if (!float.IsFinite(body.Position.X) || !float.IsFinite(body.Position.Y) || CollisionSystem.Collides(world, body.Position, body.BodyHeight))
            throw new InvalidOperationException("Physics benchmark produced an invalid player state.");
    }
}
