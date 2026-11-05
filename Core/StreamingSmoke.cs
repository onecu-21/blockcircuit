using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;

namespace BlockCircuit;

/// <summary>Real OpenGL exercise of asynchronous residency, GPU lifetime, and textured falling blocks.</summary>
static class StreamingSmoke
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "blockcircuit-stream-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var world = World.CreateStreaming(2026);
        var near = new Vector3(20.5f, 10, 20.5f);
        var far = new Vector3(-88.5f, 10, -88.5f);
        Cell fallingSource = new(20, 15, 20), landed = new(20, 10, 20), marker = new(21, 11, 21);
        var times = new List<double>();
        int phase = 0, frames = 0, maximumJobs = 0, maximumUploads = 0;
        bool sawFall = false, workerGuard = false, wrotePreview = false;
        var clock = new GameClock();
        var streamer = new WorldStreamer(world, Path.Combine(directory, "world.json"),
            new StreamingOptions { MaxLoadJobs = 2, MaxResidentColumns = 16 });
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(960, 640, "BlockCircuit | streaming and gravity verification");
        Raylib.SetTargetFPS(60);
        var renderer = new BlockRenderer { DrawDistance = 64 };
        try
        {
            while (frames < 900)
            {
                long frameStart = Stopwatch.GetTimestamp();
                Vector3 position = phase is 2 or 3 ? far : near;
                streamer.Update(position, 0);
                clock.Advance(1d / 60, _ => world.Tick());
                if (phase == 0 && world.IsCellAvailable(fallingSource) && renderer.BackgroundMeshCompletions > 0)
                {
                    using var payload = JsonDocument.Parse("{\"smoke\":\"gravity-state\"}");
                    world.Set(fallingSource, Block.Sand); world.SetBlockData(fallingSource, payload.RootElement); phase = 1;
                }
                if (phase == 1 && world.FallingBlocks.Active.Count > 0) sawFall = true;
                if (phase == 1 && sawFall && world.Get(landed) == Block.Sand && world.FallingBlocks.Active.Count == 0)
                { world.Set(marker, Block.RedWool); phase = 2; }
                if (phase == 2 && streamer.LoadedColumns.Contains(new(-6, -6))) phase = 3;
                if (phase == 3 && !streamer.LoadedColumns.Contains(new(1, 1))) phase = 4;
                if (phase == 4 && streamer.LoadedColumns.Contains(new(1, 1)) && renderer.PendingMeshJobs == 0)
                {
                    Require(world.Get(marker) == Block.RedWool && world.Get(landed) == Block.Sand && world.Get(fallingSource) == Block.Air,
                        "returning to a saved column restores its edited blocks and settled sand");
                    Require(world.GetBlockData(landed)?.GetProperty("smoke").GetString() == "gravity-state",
                        "settled block payload survives unloading and disk reload");
                    phase = 5;
                }

                var camera = new Camera3D { Position = position + new Vector3(13, 15, 17), Target = position + new Vector3(0, 2, 0),
                    Up = Vector3.UnitY, FovY = 70, Projection = CameraProjection.Perspective };
                Raylib.BeginDrawing(); Raylib.ClearBackground(BlockRenderer.HorizonColor); renderer.DrawSky(camera, 960, 640);
                Raylib.BeginMode3D(camera); renderer.DrawWorld(world, camera, 1.5f);
                renderer.DrawFallingBlocks(world, clock.Alpha, camera); Raylib.EndMode3D();
                Raylib.DrawText($"STREAMING / GRAVITY  phase {phase}  columns {streamer.ResidentColumnCount}  falling {world.FallingBlocks.Active.Count}", 20, 20, 18, Color.White);
                Raylib.EndDrawing();
                maximumJobs = Math.Max(maximumJobs, streamer.PendingJobs);
                maximumUploads = Math.Max(maximumUploads, renderer.MeshBuildsThisFrame);
                Require(renderer.MeshBuildsThisFrame <= BlockRenderer.MaximumChunkUploadsPerFrame && streamer.PendingJobs <= 2,
                    "per-frame CPU worker and GPU upload budgets");
                if (!workerGuard && renderer.BackgroundMeshCompletions > 0)
                {
                    try { Task.Run(() => renderer.DrawWorld(world, camera, 1.5f)).GetAwaiter().GetResult(); }
                    catch (InvalidOperationException) { workerGuard = true; }
                    Require(workerGuard, "GPU entry points reject worker-thread access before native calls");
                }
                if (!wrotePreview && world.FallingBlocks.Active.Count > 0)
                { Raylib.TakeScreenshot("falling-preview.png"); wrotePreview = true; }
                times.Add(Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
                frames++;
                if (phase == 5 && frames >= 150 && renderer.PendingMeshJobs == 0) break;
            }
            Require(phase == 5 && sawFall && wrotePreview && streamer.UnloadedColumns > 0,
                "streamed reentry and animated falling complete within the bounded smoke run");
            Require(renderer.BackgroundMeshCompletions > 0 && renderer.ReleasedMeshCount > 0,
                "background CPU geometry uploads and evicted GPU buffers are exercised");
            Raylib.TakeScreenshot("streaming-preview.png");
            streamer.FlushSaves(near, .2f, -.1f);
        }
        finally
        {
            streamer.Dispose(); renderer.Dispose(); Raylib.CloseWindow(); Directory.Delete(directory, recursive: true);
        }
        Require(renderer.GpuBufferCount == 0 && renderer.CachedChunkCount == 0 && renderer.PendingMeshJobs == 0 &&
            renderer.GpuAllocationCount == renderer.ReleasedMeshCount, "shutdown releases all GPU buffers and CPU jobs");
        times.Sort();
        File.WriteAllText("streaming-smoke-results.json", JsonSerializer.Serialize(new
        {
            Passed = true, Frames = frames, FixedTicks = clock.TotalTicks, FallingObserved = sawFall,
            WorkerGpuCallsRejected = workerGuard, MaximumLoadJobs = maximumJobs, MaximumChunkUploadsPerFrame = maximumUploads,
            BackgroundMeshCompletions = renderer.BackgroundMeshCompletions, TotalGpuAllocations = renderer.GpuAllocationCount,
            TotalGpuReleases = renderer.ReleasedMeshCount, LiveVbosAfterDispose = renderer.GpuBufferCount,
            FrameTimeScope = "Wall time includes the 60 FPS limiter, rendering, streaming, and smoke-only assertions; one-machine observation, not a performance guarantee.",
            P95FrameMilliseconds = times[(int)((times.Count - 1) * .95)],
            P99FrameMilliseconds = times[(int)((times.Count - 1) * .99)]
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: streamed GPU/falling smoke ({frames} frames, {clock.TotalTicks} ticks; reentry, budgets, data and lifetime verified)");
    }
    static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Streaming smoke failed: " + name); }
}
