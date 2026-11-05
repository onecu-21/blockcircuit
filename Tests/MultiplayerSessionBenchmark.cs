using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Actual owner-thread host and two TCP clients, separate from codec-only throughput.</summary>
public static class MultiplayerSessionBenchmark
{
    const int DriverFps=60,ChangeFrames=240,IdleFrames=120;
    sealed record PhaseReport(int Frames,double ElapsedSeconds,long ActualTicks,long ExpectedTicks,double DroppedSeconds,
        double CpuP50Milliseconds,double CpuP95Milliseconds,double CpuP99Milliseconds,double MaximumCpuMilliseconds,
        long FramedClientSentBytes,long FramedClientReceivedBytes,long FramedClientSentMessages,long FramedClientReceivedMessages,
        long ChunkMessages,long StateMessages,int MaximumChunkMessagesPerPump,int MaximumObservedClientReceiveMessages,
        int MaximumObservedClientReceiveBytes,long AllocatedBytesIncludingIoWorkers);
    public static void Run()
    {
        var world=new World(false);world.EnableSlotInventory();
        for(int x=8;x<=32;x++)for(int z=8;z<=32;z++)world.Set(new(x,9,z),Block.Stone);
        var lever=new Cell(18,10,18);
        world.Set(lever,Block.Lever);
        for(int x=19;x<=23;x++)world.Set(new(x,10,18),Block.Wire);
        world.Set(new(24,10,18),Block.Lamp);world.Circuits.ProcessPending();
        var hostBag=world.Items!.Capture();
        var pose=new NetPose(world.Spawn(),MathF.PI,-.18f);
        var peers=new List<NetPeer>();var clients=new List<MultiplayerClient>();
        var host=new MultiplayerHost(world,0,"session-benchmark","Host",IPAddress.Loopback);
        int port=host.Port;bool socketsClosed=false;
        try
        {
            host.Pump(pose);
            for(int i=0;i<2;i++){
                var peer=NetPeer.ConnectAsync("127.0.0.1",port).GetAwaiter().GetResult();peers.Add(peer);
                clients.Add(new(peer,"Guest"+(i+1),"session-benchmark",Guid.NewGuid()));
            }
            Until(()=>clients.All(c=>c.Ready&&c.World!.Get(new(20,9,23))==Block.Stone),Pump,"initial shared terrain");
            PumpFor(250);
            long warmSent=peers.Sum(p=>p.TotalSentBytes),warmReceived=peers.Sum(p=>p.TotalReceivedBytes);
            var originalFirst=clients[0].World!.Items!.Totals();
            var originalSecond=clients[1].World!.Items!.Capture();
            var editCell=new Cell(20,10,20);
            int requestedEdits=0,requestedToggles=0;
            var change=Measure(ChangeFrames,frame=>{
                if(frame%24==12){
                    var kind=world.Get(editCell)==Block.Air?NetCommandKind.Place:NetCommandKind.Mine;
                    if(clients[0].Request(new(kind){Cell=editCell,Slot=0})<=0)throw new InvalidOperationException("Could not queue benchmark world edit.");
                    requestedEdits++;
                }
                if(frame%30==15){
                    if(clients[1].Request(new(NetCommandKind.Toggle){Cell=lever})<=0)throw new InvalidOperationException("Could not queue benchmark switch.");
                    requestedToggles++;
                }
            });
            Until(()=>clients.All(c=>c.PendingCommands==0&&c.Ready)&&
                clients.All(c=>c.World!.Get(editCell)==world.Get(editCell))&&
                clients.All(c=>c.World!.Power.GetValueOrDefault(new(24,10,18))==world.Power.GetValueOrDefault(new(24,10,18))),
                Pump,"final command and state drain");
            int grassDelta=world.Get(editCell)==Block.Grass?1:0;
            bool exactFirst=clients[0].World!.Items!.Totals().GetValueOrDefault("blockcircuit:grass")==originalFirst["blockcircuit:grass"]-grassDelta;
            bool exactSecond=clients[1].World!.Items!.Matches(originalSecond);
            bool exactHost=world.Items.Matches(hostBag);
            if(!exactFirst||!exactSecond||!exactHost||host.AcceptedCommands!=requestedEdits+requestedToggles||host.RejectedCommands!=0)
                throw new InvalidOperationException("Session benchmark did not preserve authoritative state or independent inventories.");
            var idle=Measure(IdleFrames,_=>{});
            if(idle.ChunkMessages!=0)throw new InvalidOperationException("Idle session retransmitted unchanged chunks.");
            bool budgets=new[]{change,idle}.All(p=>p.MaximumChunkMessagesPerPump<=2*MultiplayerHost.MaximumChunkMessagesPerClientPump&&
                p.MaximumObservedClientReceiveMessages<=NetworkProtocol.MaximumQueuedMessages&&p.MaximumObservedClientReceiveBytes<=NetworkProtocol.MaximumQueuedBytes);
            if(!budgets)throw new InvalidOperationException("Session benchmark exceeded the configured work/queue budget.");
            foreach(var client in clients)client.Dispose();
            host.Pump(pose);host.Dispose();
            using(var probe=new TcpClient()){
                try{probe.Connect(IPAddress.Loopback,port);}
                catch(SocketException){socketsClosed=true;}
            }
            if(!socketsClosed||peers.Any(p=>p.IsConnected))throw new InvalidOperationException("Session benchmark leaked a listener or client socket.");
            var report=new{
                TimestampUtc=DateTime.UtcNow,Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Scope="Real loopback TCP, one owner-thread authoritative host plus two clients. Actual-time 60 Hz driver and 20 TPS world ticks. CPU percentiles cover world tick, intent enqueue and all session Pumps; they exclude frame pacing waits. No drawing, GPU, disk I/O or WAN latency claim.",
                DriverFramesPerSecond=DriverFps,SimulationTicksPerSecond=20,Guests=2,WarmupMilliseconds=250,
                WarmupFramedClientSentBytes=warmSent,WarmupFramedClientReceivedBytes=warmReceived,
                ByteScope="Application frame bytes include 4-byte length prefixes, exclude TCP/IP overhead, and are counted after successful NetworkStream write/read. Background in-flight boundary messages may cross phase boundaries.",
                Change=change,Idle=idle,RequestedWorldEdits=requestedEdits,RequestedSwitches=requestedToggles,
                host.AcceptedCommands,host.RejectedCommands,ExactGuestEditDebit=exactFirst,OtherGuestInventoryPreserved=exactSecond,
                HostInventoryPreserved=exactHost,BudgetChecksPassed=budgets,IdleUnchangedChunkRetransmissions=idle.ChunkMessages,
                ListenerAndAllClientSocketsClosed=socketsClosed&&peers.All(p=>!p.IsConnected)
            };
            File.WriteAllText("multiplayer-session-benchmark-results.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS: multiplayer session benchmark: host + 2 real clients, {requestedEdits} edits / {requestedToggles} switches; change CPU P95/P99 {change.CpuP95Milliseconds:F3}/{change.CpuP99Milliseconds:F3} ms; idle chunk retransmissions 0; all sockets closed.");
        }
        finally
        {
            foreach(var client in clients)client.Dispose();
            foreach(var peer in peers)peer.Dispose();
            host.Dispose();
        }

        void Pump(){host.Pump(pose);foreach(var client in clients)client.Pump(pose);}
        void PumpFor(int milliseconds){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){Pump();Thread.Sleep(1);}}
        PhaseReport Measure(int frames,Action<int> input)
        {
            var samples=new double[frames];var driver=new GameClock();var timer=Stopwatch.StartNew();double last=0;
            long tickStart=world.TickCount,sent=peers.Sum(p=>p.TotalSentBytes),received=peers.Sum(p=>p.TotalReceivedBytes);
            long sentMessages=peers.Sum(p=>p.TotalSentMessages),receivedMessages=peers.Sum(p=>p.TotalReceivedMessages);
            long chunks=host.TotalChunkMessages,states=host.TotalStateMessages,allocation=GC.GetTotalAllocatedBytes(precise:true);
            int maxChunks=0,maxReceiveMessages=0,maxReceiveBytes=0;
            for(int frame=0;frame<frames;frame++){
                double deadline=(frame+1d)/DriverFps;
                while(timer.Elapsed.TotalSeconds<deadline)Thread.Sleep(1);
                double now=timer.Elapsed.TotalSeconds;long start=Stopwatch.GetTimestamp();
                driver.Advance(now-last,_=>world.Tick());last=now;
                input(frame);Pump();
                samples[frame]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                maxChunks=Math.Max(maxChunks,host.LastChunkMessages);
                foreach(var peer in peers){
                    maxReceiveMessages=Math.Max(maxReceiveMessages,peer.PendingIncomingMessages);
                    maxReceiveBytes=Math.Max(maxReceiveBytes,peer.PendingIncomingBytes);
                }
            }
            long allocated=GC.GetTotalAllocatedBytes(precise:true)-allocation;Array.Sort(samples);
            return new(frames,last,world.TickCount-tickStart,(long)Math.Floor(last*20+1e-8),driver.DroppedSeconds,
                samples[frames/2],samples[(int)(frames*.95)],samples[(int)(frames*.99)],samples[^1],
                peers.Sum(p=>p.TotalSentBytes)-sent,peers.Sum(p=>p.TotalReceivedBytes)-received,
                peers.Sum(p=>p.TotalSentMessages)-sentMessages,peers.Sum(p=>p.TotalReceivedMessages)-receivedMessages,
                host.TotalChunkMessages-chunks,host.TotalStateMessages-states,maxChunks,maxReceiveMessages,maxReceiveBytes,allocated);
        }
    }
    static void Until(Func<bool> condition,Action pump,string label)
    {
        var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds<10000){pump();if(condition())return;Thread.Sleep(1);}
        throw new TimeoutException("Session benchmark timed out waiting for "+label);
    }
}
