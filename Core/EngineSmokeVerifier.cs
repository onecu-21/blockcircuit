using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Real GL-context checks; does not read or overwrite the player's save.</summary>
public sealed class EngineSmokeVerifier : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"blockcircuit-gpu-"+Guid.NewGuid()+".json");
    readonly List<object> stages=[];
    long previousUploads,previousBuilds,previousAllocations,previousReleases;
    int previousBuffers,previousChunks,idleFrames;
    public const int FrameCount=125;
    public void BeforeFrame(World world,int frame)
    {
        switch(frame)
        {
            case 20: world.Set(new(20,10,20),Block.RedWool);break;
            case 40: world.Set(new(15,10,20),Block.BlueWool);break;
            case 60: world.Toggle(new(18,10,18));break;
            case 80:
                Require(!world.Chunks.Chunks.ContainsKey(new(1,1,1)),"probe starts in an empty chunk");
                world.Set(new(20,20,20),Block.Glass);break;
            case 90: world.Set(new(20,20,20),Block.Air);break;
            case 100:
                world.Save(path,world.Spawn(),MathF.PI,-.18f);
                world.Load(path);break;
            case 110: world.Set(new(20,10,20),Block.BlueWool);break;
        }
    }
    public void AfterFrame(World world,BlockRenderer renderer,int frame)
    {
        int expected=frame switch{0 or 100=>world.Chunks.Chunks.Count,20 or 60 or 80 or 110=>1,40=>2,_=>0};
        Require(renderer.MeshBuildsThisFrame==expected,$"frame {frame}: expected {expected} dirty meshes, got {renderer.MeshBuildsThisFrame}");
        if(expected==0)
        {
            Require(renderer.GpuUploadCount==previousUploads,$"frame {frame}: idle frame uploaded geometry");
            Require(renderer.TotalMeshBuilds==previousBuilds,$"frame {frame}: idle frame regenerated geometry");
            idleFrames++;
        }
        if(frame==60)Require(world.Power.GetValueOrDefault(new(24,10,18))>0,"lamp receives power and dirty mesh");
        if(frame==80)Require(renderer.CachedChunkCount==previousChunks+1&&renderer.GpuBufferCount>previousBuffers,"new sparse chunk gets GPU buffers");
        if(frame==90)Require(renderer.CachedChunkCount==previousChunks-1&&renderer.GpuBufferCount<previousBuffers&&renderer.ReleasedMeshCount>previousReleases,"removed chunk releases its GPU resources");
        if(frame==100)Require(renderer.ReleasedMeshCount>=previousReleases+previousBuffers/3,"world reload releases old GPU meshes");
        if(frame==110)Require(renderer.GpuAllocationCount==previousAllocations&&renderer.GpuUploadCount>previousUploads,"same-size edit reuses GPU buffers");
        if(frame is 0 or 20 or 40 or 60 or 80 or 90 or 100 or 110)
            stages.Add(new{Frame=frame,MeshBuilds=renderer.MeshBuildsThisFrame,Uploads=renderer.GpuUploadCount-previousUploads,
                Allocations=renderer.GpuAllocationCount-previousAllocations,Released=renderer.ReleasedMeshCount-previousReleases,
                LiveVbos=renderer.GpuBufferCount,Chunks=renderer.CachedChunkCount});
        previousUploads=renderer.GpuUploadCount;previousBuilds=renderer.TotalMeshBuilds;previousAllocations=renderer.GpuAllocationCount;
        previousReleases=renderer.ReleasedMeshCount;previousBuffers=renderer.GpuBufferCount;previousChunks=renderer.CachedChunkCount;
    }
    public void Complete(BlockRenderer renderer)
    {
        Require(renderer.GpuBufferCount==0&&renderer.CachedChunkCount==0,"Dispose releases every cached GPU mesh");
        var report=new{Passed=true,Frames=FrameCount,IdleFramesWithoutMeshBuildOrUpload=idleFrames,
            Stages=stages,LiveVbosAfterDispose=renderer.GpuBufferCount,TotalGpuMeshAllocations=renderer.GpuAllocationCount,TotalGpuMeshesReleased=renderer.ReleasedMeshCount};
        File.WriteAllText("engine-smoke-results.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS: GPU engine smoke ({FrameCount} frames, {idleFrames} idle frames without rebuild/upload; all resources released)");
    }
    static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("GPU verification failed: "+message);}
    public void Dispose(){File.Delete(path);File.Delete(path+".tmp");}
}
