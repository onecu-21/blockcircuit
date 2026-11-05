using System.Diagnostics;
using System.Net;
using System.Text.Json;
namespace BlockCircuit;

public static class MultiplayerBenchmark
{
    public static void Run()
    {
        var world=new World(false);
        for(int y=0;y<16;y++)for(int z=0;z<16;z++)for(int x=0;x<16;x++)
            world.Set(new(x,y,z),(x+y+z)%2==0?Block.Stone:Block.Dirt);
        var chunk=ChunkSaveStore.CaptureChunk(world,default);
        using var listener=new NetListener(IPAddress.Loopback,0);
        using var client=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult();
        NetPeer? server=null;MultiplayerTests.Until(()=>listener.TryAccept(out server!),"benchmark accept");
        using(server!)
        {
            const int warmup=20,samples=200;
            for(int i=0;i<warmup;i++)Exchange(i+900);
            int chunkBytes=NetworkProtocol.EncodeFrame(NetMessage.Create("chunk",chunk,1000)).Length;
            int acknowledgementBytes=NetworkProtocol.EncodeFrame(NetMessage.Create("ack",1000,1000)).Length;
            var durations=new double[samples];
            long allocatedBefore=GC.GetTotalAllocatedBytes(precise:true);
            for(int i=0;i<samples;i++)
            {
                long start=Stopwatch.GetTimestamp();
                Exchange(i+1000);
                durations[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long allocated=GC.GetTotalAllocatedBytes(precise:true)-allocatedBefore;
            Array.Sort(durations);
            var report=new{
                TimestampUtc=DateTime.UtcNow,
                Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Scope="Actual loopback TCP transfer of a captured 4096-cell chunk followed by a decoded acknowledgement. Includes both sides' JSON codec, background scheduling and owner polling at 1ms intervals. Does not measure WAN latency, rendering, gameplay server CPU, FPS or process residency.",
                Warmup=warmup,Samples=samples,CellsPerChunk=4096,
                ChunkApplicationFrameBytes=chunkBytes,AcknowledgementApplicationFrameBytes=acknowledgementBytes,
                MeasuredApplicationFrameBytes=(long)samples*(chunkBytes+acknowledgementBytes),
                P50Milliseconds=durations[samples/2],P95Milliseconds=durations[(int)(samples*.95)],P99Milliseconds=durations[(int)(samples*.99)],
                MaximumMilliseconds=durations[^1],TotalMilliseconds=durations.Sum(),
                ManagedAllocatedBytesIncludingWorkers=allocated,
                MaximumQueuedMessages=NetworkProtocol.MaximumQueuedMessages,MaximumQueuedBytes=NetworkProtocol.MaximumQueuedBytes,
                AllChunkPayloadsAndAcknowledgementsVerified=true
            };
            File.WriteAllText("multiplayer-benchmark-results.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS: multiplayer loopback benchmark: {samples} verified chunk/ack exchanges, P95/P99 {report.P95Milliseconds:F3}/{report.P99Milliseconds:F3}ms; bounded queues.");
            void Exchange(int sequence)
            {
                if(!server!.Send(NetMessage.Create("chunk",chunk,sequence)))throw new IOException("Benchmark chunk queue rejected.");
                NetMessage? received=null;MultiplayerTests.Until(()=>client.TryReceive(out received!),"benchmark chunk");
                var decoded=received!.Read<ChunkFileData>();
                if(received.Sequence!=sequence||decoded.Cells.Length!=4096||!decoded.Cells.SequenceEqual(chunk.Cells)||!decoded.Palette.SequenceEqual(chunk.Palette))
                    throw new InvalidOperationException("Decoded chunk differs from source.");
                if(!client.Send(NetMessage.Create("ack",sequence,sequence)))throw new IOException("Benchmark acknowledgement queue rejected.");
                NetMessage? acknowledgement=null;MultiplayerTests.Until(()=>server.TryReceive(out acknowledgement!),"benchmark acknowledgement");
                if(acknowledgement!.Sequence!=sequence||acknowledgement.Read<int>()!=sequence)throw new InvalidOperationException("Decoded acknowledgement differs.");
            }
        }
    }
}
