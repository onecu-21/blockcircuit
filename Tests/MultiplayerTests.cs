using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

public static class MultiplayerTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);Console.WriteLine("PASS: "+message);checks++;}
        var dedicatedWorld=new World();
        using(var dedicated=new MultiplayerHost(dedicatedWorld,0,address:IPAddress.Loopback,dedicated:true)){
            dedicated.Pump(new(dedicatedWorld.Spawn(),0,0));
            Check(dedicated.Players.Count==0,"dedicated server has no phantom local player");
            Check(!dedicated.AnyPlayerOverlaps(new(20,10,23)),"dedicated spawn anchor does not block construction");
        }
        TestTransport(Check);
        TestSessions(Check);
        Console.WriteLine($"Multiplayer tests passed: {checks}");
    }

    sealed record Probe(int Number,string Text,Vector3 Position);

    static void TestTransport(Action<bool,string> check)
    {
        using(var listener=new NetListener(IPAddress.Loopback,0))
        using(var client=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult())
        {
            NetPeer? server=null;
            Until(()=>listener.TryAccept(out server!),"loopback accept");
            using(server!)
            {
                var original=new Probe(17,"한글 ✓",new(-12.5f,64.25f,31));
                check(client.Send(NetMessage.Create("probe",original,7)),"loopback peer accepts a nonblocking queued request");
                NetMessage? request=null;
                Until(()=>server!.TryReceive(out request!),"loopback request");
                check(request!.Type=="probe"&&request.Sequence==7&&request.Read<Probe>()==original,
                    "TCP framing preserves sequence, Unicode payload and Vector3 fields");
                check(server!.Send(NetMessage.Create("reply",original,8)),"accepted peer queues a response");
                NetMessage? response=null;Until(()=>client.TryReceive(out response!),"loopback response");
                check(response!.Sequence==8&&response.Read<Probe>()==original,"real TCP connection carries an independent server response");
                client.Dispose();Until(()=>!server.IsConnected,"remote close");
                check(!server.IsConnected,"remote shutdown closes a peer without an owner-thread read blocking");
            }
        }
        using(var listener=new NetListener(IPAddress.Loopback,0))
        using(var raw=new TcpClient())
        {
            raw.Connect(IPAddress.Loopback,listener.Port);
            NetPeer? server=null;Until(()=>listener.TryAccept(out server!),"fragmented peer");
            using(server!)
            {
                byte[] data=Encoding.UTF8.GetBytes("""{"Type":"fragment","Sequence":9,"Data":{"value":"ready"}}""");
                byte[] frame=Frame(data);
                var stream=raw.GetStream();
                stream.Write(frame.AsSpan(0,2));
                check(!server!.TryReceive(out _),"a partial TCP prefix is not published as a message");
                stream.Write(frame.AsSpan(2,4));
                check(!server.TryReceive(out _),"an incomplete TCP JSON body is not published");
                stream.Write(frame.AsSpan(6));
                NetMessage? message=null;Until(()=>server.TryReceive(out message!),"fragmented message");
                check(message!.Data.GetProperty("value").GetString()=="ready","arbitrarily fragmented prefix and JSON body reassemble exactly once");
                check(!server.TryReceive(out _),"fragment reassembly does not duplicate a message");
            }
        }
        RejectFrame(Frame([0x7B]),"malformed JSON closes only the offending connection");
        RejectFrame(Prefix(0),"zero-length wire frames are rejected");
        RejectFrame(Prefix(NetworkProtocol.MaximumFrameBytes+1),"oversized frame headers are rejected before allocating the advertised body");
        RejectFrame(Frame(Encoding.UTF8.GetBytes("""{"Type":"probe","Type":"other","Sequence":0,"Data":{}}""")),"duplicate envelope fields are rejected");
        RejectFrame(Frame(Encoding.UTF8.GetBytes("""{"Type":"probe","Sequence":-1,"Data":{}}""")),"negative wire sequences are rejected");
        using(var listener=new NetListener(IPAddress.Loopback,0))
        using(var raw=new TcpClient())
        {
            raw.Connect(IPAddress.Loopback,listener.Port);
            NetPeer? server=null;Until(()=>listener.TryAccept(out server!),"truncated peer");
            using(server!)
            {
                var stream=raw.GetStream();stream.Write(Prefix(100));stream.Write([0x7B,0x22,0x78]);
                raw.Client.Shutdown(SocketShutdown.Send);
                Until(()=>!server!.IsConnected,"truncated read");
                check(server!.Error is not null,"EOF in a framed JSON body closes the peer with an observed failure");
            }
        }
        using(var listener=new NetListener(IPAddress.Loopback,0))
        using(var client=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult())
        {
            NetPeer? server=null;Until(()=>listener.TryAccept(out server!),"dispose peer");
            listener.Dispose();Until(()=>!client.IsConnected,"listener disposal");
            check(!client.IsConnected&&!server!.IsConnected,"listener disposal closes accepted sessions and their remote sockets");
        }
        using(var listener=new NetListener(IPAddress.Loopback,0))
        using(var raw=new TcpClient())
        {
            raw.Connect(IPAddress.Loopback,listener.Port);
            NetPeer? server=null;Until(()=>listener.TryAccept(out server!),"overload peer");
            using(server!)
            {
                byte[] message=Frame(Encoding.UTF8.GetBytes("""{"Type":"probe","Sequence":1,"Data":{}}"""));
                byte[] flood=new byte[message.Length*(NetworkProtocol.MaximumQueuedMessages+16)];
                for(int i=0;i<flood.Length;i+=message.Length)message.CopyTo(flood,i);
                try{raw.GetStream().Write(flood);}catch(IOException){}
                Until(()=>!server!.IsConnected,"bounded receive queue");
                check(server!.Error is not null&&server.PendingIncomingMessages<=NetworkProtocol.MaximumQueuedMessages&&
                    server.PendingIncomingBytes<=NetworkProtocol.MaximumQueuedBytes,"an undrained receive queue disconnects its peer within message and byte budgets");
            }
        }
        using(var listener=new NetListener(IPAddress.Loopback,0,maximumPeers:1))
        using(var first=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult())
        {
            NetPeer? accepted=null;Until(()=>listener.TryAccept(out accepted!),"capacity peer");
            using(accepted!)
            using(var excess=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult())
            {
                Until(()=>!excess.IsConnected,"capacity rejection");
                check(first.IsConnected&&accepted!.IsConnected&&listener.ActivePeerCount==1,
                    "listener capacity rejects a new connection while preserving its existing peer");
            }
        }
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();bool observed=false;
            try{using var ignored=NetPeer.ConnectAsync("127.0.0.1",1,canceled.Token).GetAwaiter().GetResult();}
            catch(OperationCanceledException){observed=true;}
            check(observed,"connection cancellation is propagated without leaving a live socket");
        }

        void RejectFrame(byte[] frame,string label)
        {
            using var listener=new NetListener(IPAddress.Loopback,0);
            using var raw=new TcpClient();raw.Connect(IPAddress.Loopback,listener.Port);
            NetPeer? server=null;Until(()=>listener.TryAccept(out server!),"invalid peer");
            using(server!)
            {
                raw.GetStream().Write(frame);
                Until(()=>!server!.IsConnected,label);
                check(server!.Error is not null&&!server.TryReceive(out _),label);
                using var healthy=NetPeer.ConnectAsync("127.0.0.1",listener.Port).GetAwaiter().GetResult();
                NetPeer? accepted=null;Until(()=>listener.TryAccept(out accepted!),"healthy peer after malformed input");
                accepted!.Dispose();
            }
        }
    }

    internal static byte[] Prefix(int size){var result=new byte[4];BinaryPrimitives.WriteInt32BigEndian(result,size);return result;}
    internal static byte[] Frame(byte[] body){var result=new byte[body.Length+4];Prefix(body.Length).CopyTo(result,0);body.CopyTo(result,4);return result;}
    internal static void Until(Func<bool> condition,string label,int milliseconds=10000,Action? pump=null)
    {
        var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds<milliseconds){pump?.Invoke();if(condition())return;Thread.Sleep(1);}
        throw new TimeoutException("Timed out waiting for "+label);
    }

    static void TestSessions(Action<bool,string> check)
    {
        string directory=Path.Combine(Path.GetTempPath(),"blockcircuit-network-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var world=new World(false);world.EnableSlotInventory();
        for(int x=8;x<=32;x++)for(int z=8;z<=32;z++)world.Set(new(x,9,z),Block.Stone);
        var lever=new Cell(18,10,18);world.Set(lever,Block.Lever);
        for(int x=19;x<=23;x++)world.Set(new(x,10,18),Block.Wire);
        world.Set(new(24,10,18),Block.Lamp);
        var bench=new Cell(18,10,21);world.Set(bench,Block.CraftingTable);
        world.Circuits.ProcessPending();
        var hostBag=world.Items!.Capture();
        var pose=new NetPose(world.Spawn(),MathF.PI,-.18f);
        string solo=Path.Combine(directory,"singleplayer-sentinel.json");
        File.WriteAllText(solo,"unrelated personal save");
        byte[] soloBefore=File.ReadAllBytes(solo);
        Guid aliceIdentity=Guid.NewGuid(),bobIdentity=Guid.NewGuid();
        using var host=new MultiplayerHost(world,port:0,password:"test-key",hostName:"Host",address:IPAddress.Loopback);
        MultiplayerClient? alice=null,bob=null;
        var clients=new List<MultiplayerClient>();
        host.Pump(pose);
        try
        {
            alice=Connect("Alice",aliceIdentity);
            Until(()=>alice.Ready&&alice.World!.Get(new(20,9,23))==Block.Stone,"first authoritative world",pump:Pump);
            check(alice.World!.IsNetworkReplica&&alice.World.Items is not null&&alice.World.Items!=world.Items,
                "a real guest receives an ephemeral world replica with a separate inventory");
            int aliceGrass=alice.World.Items!.Totals()["blockcircuit:grass"];
            var placed=new Cell(20,10,20);
            check(Command(alice,new(NetCommandKind.Place){Cell=placed,Slot=0}).Accepted,
                "host accepts an in-range placement backed by the guest's real selected stack");
            Until(()=>alice.World.Get(placed)==Block.Grass&&alice.World.Items.Totals()["blockcircuit:grass"]==aliceGrass-1,
                "authoritative placement reply",pump:Pump);
            check(world.Get(placed)==Block.Grass&&world.Items!.Matches(hostBag),
                "guest placement changes shared terrain without debiting the host's bag");

            bob=Connect("Bob",bobIdentity);
            Until(()=>bob.Ready&&bob.World!.Get(placed)==Block.Grass,"late join after edit",pump:Pump);
            check(bob.World!.Get(placed)==Block.Grass&&bob.World.Get(bench)==Block.CraftingTable,
                "late join receives the current edited chunks and crafting table");
            Until(()=>alice.Players.Count>=3&&bob.Players.Count>=3,"remote avatars",pump:Pump);
            check(alice.Players.Any(p=>p.Id==bob.LocalPlayerId)&&bob.Players.Any(p=>p.Id==alice.LocalPlayerId),
                "two real guests receive each other's public avatar identities");
            check(alice.LocalPlayerId!=aliceIdentity&&bob.LocalPlayerId!=bobIdentity&&
                alice.World.NetworkPlayers.Count==0&&bob.World.NetworkPlayers.Count==0,
                "replicas do not receive private reconnect identities or the host's saved profile dictionary");

            var bobBefore=bob.World.Items!.Capture();
            check(!Command(bob,new(NetCommandKind.Place){Cell=placed,Slot=0}).Accepted,
                "conflicting placement is rejected once the cell is occupied");
            Until(()=>bob.World.Items!.Matches(bobBefore),"conflict inventory",pump:Pump);
            check(bob.World.Items!.Matches(bobBefore)&&world.Get(placed)==Block.Grass,
                "rejected conflict preserves both world state and the second guest's stack");

            PumpFor(120);
            var once=new Cell(21,10,20);var replayTarget=new Cell(22,10,20);
            check(Command(alice,new(NetCommandKind.Place){Cell=once,Slot=0,Sequence=500}).Accepted,
                "an explicit fresh request sequence commits one edit");
            Until(()=>alice.World.Get(once)==Block.Grass,"fresh sequenced edit",pump:Pump);
            int afterOnce=alice.World.Items!.Totals()["blockcircuit:grass"];
            check(!Command(alice,new(NetCommandKind.Place){Cell=replayTarget,Slot=0,Sequence=500}).Accepted,
                "a duplicate request sequence is rejected even when its payload changes");
            PumpFor(80);
            check(world.Get(replayTarget)==Block.Air&&alice.World.Items.Totals()["blockcircuit:grass"]==afterOnce,
                "duplicate replay cannot apply another edit or debit another item");
            world.Set(new(1,10,1),Block.Dirt);
            check(!Command(alice,new(NetCommandKind.Mine){Cell=new(1,10,1)}).Accepted&&world.Get(new(1,10,1))==Block.Dirt,
                "a far-away mine request cannot mutate terrain outside the guest's reach");
            check(!Command(alice,new(NetCommandKind.Place){Cell=replayTarget,Slot=99}).Accepted&&world.Get(replayTarget)==Block.Air,
                "an invalid hotbar slot is rejected before changing terrain");

            PumpFor(120);
            check(Command(alice,new(NetCommandKind.Toggle){Cell=lever}).Accepted,"a reachable shared lever can be toggled by a guest");
            Until(()=>bob.World.Power.GetValueOrDefault(new(24,10,18))>0,"shared circuit power",pump:Pump);
            check(world.Power.GetValueOrDefault(new(24,10,18))>0&&bob.World.Power.GetValueOrDefault(new(24,10,18))>0,
                "authoritative circuit changes reach another guest's replica");

            check(Command(alice,new(NetCommandKind.Assign){Item="blockcircuit:oak_log",Slot=0}).Accepted,
                "library assignment moves owned guest stock into a real hotbar slot");
            Until(()=>alice.World.Items!.Slots[0]?.Id==new ContentId("blockcircuit:oak_log"),"assigned log",pump:Pump);
            int logsBefore=alice.World.Items!.Totals()["blockcircuit:oak_log"];
            int planksBefore=alice.World.Items.Totals()["blockcircuit:oak_planks"];
            check(Command(alice,new(NetCommandKind.Click){Target=new(ItemArea.Storage,0)}).Accepted&&
                Command(alice,new(NetCommandKind.Click){Target=new(ItemArea.Craft2,0)}).Accepted,
                "guest cursor and personal crafting inputs are changed through server intents");
            Until(()=>alice.World.Items!.Crafting2[0] is not null&&alice.World.Items.Cursor is null,"server crafting inputs",pump:Pump);
            check(Command(alice,new(NetCommandKind.Craft){Size=2,ToStorage=true}).Accepted,"host validates a guest's 2x2 crafting result");
            Until(()=>alice.World.Items!.Totals()["blockcircuit:oak_log"]==logsBefore-1&&
                alice.World.Items.Totals()["blockcircuit:oak_planks"]==planksBefore+4,"guest craft conservation",pump:Pump);
            check(alice.World.Items!.Totals()["blockcircuit:oak_log"]==logsBefore-1&&
                alice.World.Items.Totals()["blockcircuit:oak_planks"]==planksBefore+4&&world.Items!.Matches(hostBag)&&bob.World.Items!.Matches(bobBefore),
                "server crafting consumes exactly one guest input without touching other players' bags");

            check(Command(bob,new(NetCommandKind.Assign){Item="blockcircuit:coal",Slot=0}).Accepted&&
                Command(bob,new(NetCommandKind.Click){Target=new(ItemArea.Storage,0)}).Accepted,
                "a second guest independently selects and carries a real coal stack");
            check(Command(bob,new(NetCommandKind.Drag){Targets=Enumerable.Range(0,9).Select(i=>new ItemSlot(ItemArea.Craft3,i)).ToArray(),Right=true}).Accepted&&
                Command(bob,new(NetCommandKind.Click){Target=new(ItemArea.Storage,0)}).Accepted,
                "authoritative right drag distributes one input into each 3x3 crafting cell");
            var beforeRejectedBench=bob.World.Items!.Capture();
            check(!Command(bob,new(NetCommandKind.Craft){Size=3,Bench=new(1,10,1),ToStorage=true}).Accepted&&bob.World.Items.Matches(beforeRejectedBench),
                "3x3 crafting without a reachable real crafting table preserves every input");
            int coalBefore=bob.World.Items.Totals()["blockcircuit:coal"],blocksBefore=bob.World.Items.Totals()["blockcircuit:coal_block"];
            check(Command(bob,new(NetCommandKind.Craft){Size=3,Bench=bench,ToStorage=true}).Accepted&&
                bob.World.Items.Totals()["blockcircuit:coal"]==coalBefore-9&&bob.World.Items.Totals()["blockcircuit:coal_block"]==blocksBefore+1,
                "a valid guest 3x3 craft consumes exactly nine ingredients and grants one output");
            int bobCoal=bob.World.Items.Totals()["blockcircuit:coal"];
            check(Command(bob,new(NetCommandKind.Shift){Source=new(ItemArea.Storage,0)}).Accepted&&bob.World.Items.Slots[0] is null&&
                bob.World.Items.Totals()["blockcircuit:coal"]==bobCoal,
                "guest shift transfer moves hotbar contents into the backpack without changing quantity");

            int overallBefore=OwnedAndDropped("blockcircuit:oak_log");
            check(Command(alice,new(NetCommandKind.Drop){Source=new(ItemArea.Craft2,0)}).Accepted,
                "host creates one shared pickup for a guest drop intent");
            Until(()=>world.DroppedItems.Count>0&&bob.World.DroppedItems.Count>0,"shared dropped pickup",pump:Pump);
            check(OwnedAndDropped("blockcircuit:oak_log")==overallBefore&&world.DroppedItems.Count==1,
                "dropping transfers one unit from the guest bag into shared pickup state without duplication");

            var savedBag=alice.World.Items!.Capture();Guid oldPublicId=alice.LocalPlayerId;
            clients.Remove(alice);alice.Dispose();alice=null;
            Until(()=>!host.Players.Any(p=>p.Id==oldPublicId),"guest departure",pump:Pump);
            check(!host.Players.Any(p=>p.Id==oldPublicId),"disconnect removes the departing avatar from the host roster");
            alice=Connect("Alice",aliceIdentity);
            Until(()=>alice.Ready&&alice.World!.Items!.Matches(savedBag),"reconnect inventory",pump:Pump);
            check(alice.World!.Items!.Matches(savedBag),"reconnecting with the same private profile restores inventory, cursor and crafting inputs");

            using(var wrongPassword=Connect("WrongKey",Guid.NewGuid(),password:"wrong"))
            {
                Until(()=>wrongPassword.Error is not null,"password rejection",pump:()=>{Pump();wrongPassword.Pump(pose);});
                check(!wrongPassword.Ready&&wrongPassword.World is null,"wrong password is rejected before a replica replaces the local world");
            }
            var different=GameRegistries.Create(r=>r.Items.Register(new("test:network_mismatch"),new("Mismatch")));
            using(var incompatible=Connect("Mismatch",Guid.NewGuid(),registries:different))
            {
                Until(()=>incompatible.Error is not null,"registry rejection",pump:()=>{Pump();incompatible.Pump(pose);});
                check(!incompatible.Ready&&incompatible.World is null,"content fingerprint mismatch is rejected before world creation");
            }
            using(var duplicate=Connect("Duplicate",aliceIdentity))
            {
                Until(()=>duplicate.Error is not null,"duplicate active profile",pump:Pump);
                check(!duplicate.Ready&&alice.IsConnected,"an already connected private profile cannot open a second active session");
            }
            using(var malformed=Connect("Malformed",Guid.NewGuid()))
            {
                Until(()=>malformed.Ready,"malformed-command guest initial sync",pump:Pump);
                malformed.Request(new((NetCommandKind)999){Cell=placed});
                Until(()=>malformed.Error is not null,"unknown command kind rejection",pump:Pump);
                check(!malformed.Ready&&alice.IsConnected&&bob.IsConnected,"unknown command kinds disconnect only the offending guest");
            }
            using(var raw=NetPeer.ConnectAsync("127.0.0.1",host.Port).GetAwaiter().GetResult())
            {
                Guid blockedIdentity=Guid.NewGuid();
                var fingerprint=MultiplayerContent.Fingerprint(world.Registries);
                raw.Send(NetMessage.Create("hello",new NetHello(NetworkProtocol.Version,fingerprint,"BlockedBatch","wrong",blockedIdentity)));
                raw.Send(NetMessage.Create("hello",new NetHello(NetworkProtocol.Version,fingerprint,"BlockedBatch","test-key",blockedIdentity)));
                Thread.Sleep(30);
                NetMessage? response=null;Until(()=>raw.TryReceive(out response!),"batched rejected hello",pump:Pump);
                PumpFor(30);
                check(response!.Type=="failure"&&!world.NetworkPlayers.ContainsKey(MultiplayerContent.IdentityKey(blockedIdentity))&&
                    host.Players.All(p=>p.Name!="BlockedBatch"),"a failed hello followed by a valid hello in one batch cannot activate the rejected profile");
            }
            using(var raw=NetPeer.ConnectAsync("127.0.0.1",host.Port).GetAwaiter().GetResult())
            {
                raw.Send(NetMessage.Create("hello",new NetHello(NetworkProtocol.Version+1,MultiplayerContent.Fingerprint(world.Registries),"WrongVersion","test-key",Guid.NewGuid())));
                NetMessage? response=null;Until(()=>raw.TryReceive(out response!),"protocol version rejection",pump:Pump);
                check(response!.Type=="failure","a mismatched protocol version is rejected during handshake");
            }
            using(var raw=NetPeer.ConnectAsync("127.0.0.1",host.Port).GetAwaiter().GetResult())
            {
                var movementClock=Stopwatch.StartNew();
                raw.Send(NetMessage.Create("hello",new NetHello(NetworkProtocol.Version,MultiplayerContent.Fingerprint(world.Registries),"MovementProbe","test-key",Guid.NewGuid())));
                NetWelcome? welcome=null;
                Until(()=>{
                    while(raw.TryReceive(out var message))if(message.Type=="welcome"){welcome=message.Read<NetWelcome>();return true;}
                    return false;
                },"movement probe welcome",pump:Pump);
                var start=welcome!.Spawn;
                for(int i=1;i<=40;i++)raw.Send(NetMessage.Create("pose",start with{Position=start.Position+new Vector3(i*.2f,0,0)}));
                for(int i=0;i<12;i++){Pump();Thread.Sleep(1);}
                var accepted=host.Players.Single(p=>p.Id==welcome.PlayerId);
                double elapsed=movementClock.Elapsed.TotalSeconds;
                check(Vector3.Distance(accepted.Position,start.Position)<=2.5+32*elapsed+.3,
                    "a burst of individually small pose packets cannot multiply the server's cumulative movement allowance");
            }

            string save=Path.Combine(directory,"host.json");
            world.Save(save,world.Spawn(),0,0);
            var restored=new World(false);restored.Load(save);
            check(restored.NetworkPlayers.Count>=2&&restored.NetworkPlayers.Values.Any(p=>JsonSerializer.Serialize(p.Inventory)==JsonSerializer.Serialize(savedBag)),
                "host save retains guest inventories for reconnect after loading the world");
            check(soloBefore.SequenceEqual(File.ReadAllBytes(solo))&&world.Items!.Matches(hostBag),
                "guest sessions and host snapshots leave an unrelated singleplayer save and the host inventory unchanged");
            byte[] healthyManifest=File.ReadAllBytes(save);
            var malformedProfile=JsonNode.Parse(healthyManifest)!.AsObject();
            var profiles=malformedProfile["NetworkPlayers"]!.AsObject();
            profiles.First().Value!["Name"]=new string('X',25);
            File.WriteAllText(save,malformedProfile.ToJsonString());File.Delete(save+".bak");
            string originalProfiles=JsonSerializer.Serialize(restored.CaptureNetworkPlayers());
            string originalBag=JsonSerializer.Serialize(restored.CaptureInventory());
            check(InventoryTests.Rejected(()=>restored.Load(save))&&JsonSerializer.Serialize(restored.CaptureNetworkPlayers())==originalProfiles&&
                JsonSerializer.Serialize(restored.CaptureInventory())==originalBag&&restored.Get(placed)==Block.Grass,
                "invalid saved guest profiles reject loading before replacing world cells or any player's inventory");
            File.WriteAllBytes(save,healthyManifest);
            bool ownerRejected=false;try{Task.Run(()=>host.Pump(pose)).GetAwaiter().GetResult();}catch(InvalidOperationException){ownerRejected=true;}
            check(ownerRejected&&world.Items!.Matches(hostBag),"a worker thread cannot pump the authoritative world session");
            int overallBeforePickup=OwnedAndDropped("blockcircuit:oak_log");
            for(int tick=0;tick<20;tick++)world.Tick();
            Until(()=>world.DroppedItems.Count==0,"authoritative pickup",pump:Pump);
            check(OwnedAndDropped("blockcircuit:oak_log")==overallBeforePickup,"delayed authoritative pickup conserves combined guest bags and shared drops");
            TestDistantGuestStreaming(check,directory);
        }
        finally
        {
            foreach(var client in clients)client.Dispose();
            host.Dispose();
            string full=Path.GetFullPath(directory);
            if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||
                !Path.GetFileName(full).StartsWith("blockcircuit-network-tests-",StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected network test cleanup path.");
            Directory.Delete(full,true);
        }

        MultiplayerClient Connect(string name,Guid identity,string password="test-key",GameRegistries? registries=null)
        {
            var peer=NetPeer.ConnectAsync("127.0.0.1",host.Port).GetAwaiter().GetResult();
            var client=new MultiplayerClient(peer,name,password,identity,registries);
            clients.Add(client);return client;
        }
        void Pump()
        {
            host.Pump(pose);
            foreach(var client in clients.ToArray())client.Pump(pose);
        }
        void PumpFor(int milliseconds)
        {
            var timeout=Stopwatch.StartNew();while(timeout.ElapsedMilliseconds<milliseconds){Pump();Thread.Sleep(1);}
        }
        NetCommandResult Command(MultiplayerClient client,NetCommand command)
        {
            var previous=client.LastReply;
            long sequence=client.Request(command);
            if(sequence<=0)throw new InvalidOperationException("Connected guest could not queue "+command.Kind);
            Until(()=>client.LastReply is {} reply&&reply.Sequence==sequence&&!ReferenceEquals(reply,previous)&&client.LastAcknowledgedSequence>=sequence,
                command.Kind+" reply",pump:Pump);
            return client.LastReply!;
        }
        int OwnedAndDropped(string id)
        {
            int count=world.Items!.Totals().GetValueOrDefault(id);
            foreach(var saved in world.NetworkPlayers.Values)
                count+=PlayerInventory.Restore(world.Registries.Items,saved.Inventory).Totals().GetValueOrDefault(id);
            count+=world.DroppedItems.Where(p=>p.Item==id).Sum(p=>p.Count);return count;
        }
    }

    static void TestDistantGuestStreaming(Action<bool,string> check,string directory)
    {
        var world=World.CreateStreaming(2026);
        var options=new StreamingOptions{MaxResidentColumns=4,MaxLoadJobs=2,MaxIntegrationsPerFrame=1,MaxUnloadsPerFrame=1,MaxSaveCapturesPerFrame=2,UnloadMargin=0};
        string path=Path.Combine(directory,"distant-host.json");
        using var streamer=new WorldStreamer(world,path,options);
        using var host=new MultiplayerHost(world,port:0,hostName:"StreamingHost",address:IPAddress.Loopback);
        var originalPose=new NetPose(new(8.5f,96,8.5f),0,0,true);
        var hostPose=originalPose;
        var guestPose=originalPose;
        MultiplayerClient? guest=null;
        int maxColumns=0,maxJobs=0,maxIntegrations=0,maxUnloads=0,maxCaptures=0,maxChunkMessages=0;
        Guid identity=Guid.NewGuid();
        try
        {
            Until(()=>world.IsCellAvailable(new(8,80,8)),"initial streamed column",pump:Pump);
            guest=Connect();Until(()=>guest.Ready,"initial streamed guest",pump:Pump);
            var marker=new Cell(3,80,3);world.Set(marker,Block.DiamondBlock);
            Until(()=>guest.World!.Get(marker)==Block.DiamondBlock,"guest edit marker",pump:Pump);
            hostPose=new(new(328.5f,96,-311.5f),0,0,true);
            Until(()=>world.IsCellAvailable(new(328,80,-312))&&world.IsCellAvailable(marker),"separate host and guest interests",pump:Pump);
            check(host.InterestPositions.Count==1&&world.IsCellAvailable(marker)&&guest.World!.Get(marker)==Block.DiamondBlock,
                "a real distant guest keeps its own column available when the host moves far away");
            check(maxColumns<=4&&maxJobs<=2&&maxIntegrations<=1&&maxUnloads<=1&&maxCaptures<=2&&maxChunkMessages<=2,
                "multiple player interests preserve streaming and per-client chunk publication budgets");
            guest.Dispose();guest=null;
            Until(()=>host.ClientCount==0&&!streamer.LoadedColumns.Contains(new(0,0)),"departed guest column save and unload",pump:Pump);
            check(world.StoredChunkReferences.Keys.Any(c=>c.X==0&&c.Z==0)&&!world.IsCellAvailable(marker),
                "a departed distant guest's dirty column is saved and unloaded after its interest disappears");
            guest=Connect();
            Until(()=>guest.Ready&&guest.World!.Get(marker)==Block.DiamondBlock,"saved guest location reentry",pump:Pump);
            check(world.Get(marker)==Block.DiamondBlock&&guest.World!.Get(marker)==Block.DiamondBlock,
                "reconnecting to an unloaded saved location reloads the edited terrain before the replica becomes ready");
        }
        finally{guest?.Dispose();}
        MultiplayerClient Connect()=>new(NetPeer.ConnectAsync("127.0.0.1",host.Port).GetAwaiter().GetResult(),"DistantGuest","",identity);
        void Pump()
        {
            host.Pump(hostPose);
            streamer.Update(hostPose.Position,0,hostPose.Yaw,hostPose.Pitch,host.InterestPositions);
            host.Pump(hostPose);
            guest?.Pump(guestPose);
            maxColumns=Math.Max(maxColumns,streamer.ResidentColumnCount);maxJobs=Math.Max(maxJobs,streamer.PendingJobs);
            maxIntegrations=Math.Max(maxIntegrations,streamer.LastIntegrations);maxUnloads=Math.Max(maxUnloads,streamer.LastUnloads);
            maxCaptures=Math.Max(maxCaptures,streamer.LastSaveCaptures);maxChunkMessages=Math.Max(maxChunkMessages,host.LastChunkMessages);
            if(streamer.LastError is not null)throw new InvalidOperationException("Distant guest stream failed.",streamer.LastError);
        }
    }

}
