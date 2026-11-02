using Raylib_cs;

sealed partial class Game
{
    void DrawEngineOverlay()
    {
        Raylib.DrawRectangle(20,122,345,231,new Color(14,22,32,228));
        Raylib.DrawText("ENGINE  /  F3",34,135,17,accent);
        string[] lines=
        [
             $"Ticks {clock.TotalTicks} | steps {clock.StepsThisFrame} | dropped {clock.DroppedSeconds:0.00}s",
            $"Frame {Raylib.GetFrameTime()*1000:0.00} ms | CPU submit {rendererCpuMilliseconds:0.00} ms",
            $"Chunks {renderer.VisibleChunkCount} visible / {renderer.CachedChunkCount} cached",
            $"Culled {renderer.CulledChunkCount} | sparse storage {world.Chunks.Chunks.Count}",
            $"Vertices {renderer.VertexCount:N0} | triangles {renderer.TriangleCount:N0}",
            $"Mesh rebuilds {renderer.MeshBuildsThisFrame} | {renderer.LastBuildMilliseconds:0.00} ms",
            $"GPU uploads total {renderer.GpuUploadCount} | VBOs {renderer.GpuBufferCount}",
            $"Managed heap {GC.GetTotalMemory(false)/1048576.0:0.0} MiB",
             $"Circuit updates {world.Circuits.TotalNetworkUpdates} | undo {world.UndoCount}",
            $"GC collections {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}"
        ];
        for(int i=0;i<lines.Length;i++)Raylib.DrawText(lines[i],34,160+i*18,13,Color.White);
    }
}
