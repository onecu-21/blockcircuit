using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Semantic compatibility hash. Runtime IDs and OS line endings are deliberately excluded.</summary>
public static class MultiplayerContent
{
    public static string Fingerprint(GameRegistries registries)
    {
        var parts=new List<string>{"blockcircuit-net-"+NetworkProtocol.Version,"terrain-"+StreamingTerrainGenerator.Version};
        for(int i=0;i<registries.Blocks.Count;i++){
            var b=(Block)i;var info=registries.Blocks.GetInfo(b);
            parts.Add("B|"+registries.Blocks.GetId(b)+"|"+info.Pattern+"|"+info.Hardness.ToString("R",CultureInfo.InvariantCulture)+"|"+info.Transparent+"|"+info.AffectedByGravity+"|"+info.Tint+"|"+registries.Blocks.GetId(info.TextureBlock));
        }
        for(int i=0;i<registries.Items.Count;i++)parts.Add("I|"+registries.Items.GetId(i)+"|"+registries.Items.Get(i).MaxStack);
        for(int i=0;i<registries.EntityTypes.Count;i++)parts.Add("E|"+registries.EntityTypes.GetId(i));
        parts.Sort(StringComparer.Ordinal);
        using var recipes=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Assets","recipes.json")));
        parts.Add(JsonSerializer.Serialize(recipes.RootElement));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",parts))));
    }
    public static string IdentityKey(Guid privateIdentity)=>Convert.ToHexString(SHA256.HashData(privateIdentity.ToByteArray()));
    internal static bool ValidName(string? name)=>!string.IsNullOrWhiteSpace(name)&&name.Length<=24&&name.All(c=>c is >= ' ' and <= '~');
    internal static bool ValidPose(NetPose? pose)=>pose is not null&&float.IsFinite(pose.Position.X)&&float.IsFinite(pose.Position.Y)&&float.IsFinite(pose.Position.Z)&&
        Math.Abs(pose.Position.X)<=World.MaximumPlayerCoordinate&&Math.Abs(pose.Position.Z)<=World.MaximumPlayerCoordinate&&pose.Position.Y is >=0 and <=255&&
        float.IsFinite(pose.Yaw)&&float.IsFinite(pose.Pitch)&&Math.Abs(pose.Pitch)<=1.5f;
}

/// <summary>All World access occurs in Pump on the creating thread; transport only queues immutable DTOs.</summary>
public sealed class MultiplayerHost:IDisposable
{
    public const int MaximumClients=8,ReplicationRadius=2,MaximumChunkMessagesPerClientPump=2;
    readonly World world;
    readonly NetListener listener;
    readonly string password,fingerprint,hostName;
    readonly RecipeBook recipes;
    readonly int owner=Environment.CurrentManagedThreadId;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly List<Player> connections=[];
    NetPose hostPose;
    bool disposed;
    int fairness;
    long lastCollectionTick=-1;
    public Guid LocalPlayerId {get;}=Guid.NewGuid();
    public int Port=>listener.Port;
    public int ClientCount=>connections.Count(p=>p.Active);
    public string Status=>disposed?"Closed":$"Hosting port {Port} / {ClientCount} guests";
    public string? Error=>listener.Error?.Message;
    public long AcceptedCommands {get;private set;}
    public long RejectedCommands {get;private set;}
    public int LastChunkMessages {get;private set;}
    public long TotalChunkMessages {get;private set;}
    public long TotalStateMessages {get;private set;}
    public World World=>world;
    public IReadOnlyList<Vector3> InterestPositions=>connections.Where(p=>p.Active).Select(p=>p.Pose.Position).ToArray();
    public IReadOnlyList<RemotePlayerState> Players=>Roster();
    sealed record Stamp(Chunk Chunk,long ContentRevision,string Switches);
    sealed class Player(NetPeer peer,double now)
    {
        public NetPeer Peer=peer;
        public double Joined=now,RejectedUntil,LastPose=now,LastState=-1,RateStart=now,NextMine,NextPlace,BudgetAt=now;
        public double HorizontalBudget=2.5,VerticalBudget=8;
        public int CommandsThisSecond;
        public bool Active,NeedsSpawnResolution,CorrectPosition;
        public string Name="",Key="";
        public Guid Id;
        public NetPose Pose=new(new Vector3(20.5f,10.01f,23.5f),0,0);
        public PlayerInventory Bag=null!;
        public long Sequence;
        public readonly Dictionary<ChunkCoord,Stamp> Sent=[];
    }
    public MultiplayerHost(World world,int port=25565,string password="",string hostName="Host",IPAddress? address=null)
    {
        this.world=world??throw new ArgumentNullException(nameof(world));
        if(world.IsNetworkReplica)throw new ArgumentException("A replica cannot host a world.");
        if(!MultiplayerContent.ValidName(hostName)||password is null||password.Length>64)throw new ArgumentException("Invalid multiplayer name or password.");
        this.password=password;this.hostName=hostName;world.EnableSlotInventory();world.ClearHistory();
        fingerprint=MultiplayerContent.Fingerprint(world.Registries);recipes=RecipeBook.LoadDefault(world.Registries.Items);
        hostPose=new(world.Spawn(),0,0);
        listener=new NetListener(address??IPAddress.Any,port,MaximumClients);
    }
    void CheckOwner(){if(Environment.CurrentManagedThreadId!=owner)throw new InvalidOperationException("Multiplayer sessions belong to their owner thread.");}
    public bool AnyPlayerOverlaps(Cell cell)=>CollisionSystem.OverlapsPlayer(cell,hostPose.Position)||connections.Any(p=>p.Active&&CollisionSystem.OverlapsPlayer(cell,p.Pose.Position));
    public void Pump(NetPose localPose)
    {
        CheckOwner();ObjectDisposedException.ThrowIf(disposed,this);
        if(!MultiplayerContent.ValidPose(localPose))throw new ArgumentException("Invalid host pose.");
        hostPose=localPose;double now=clock.Elapsed.TotalSeconds;LastChunkMessages=0;
        while(listener.TryAccept(out var peer)){
            if(connections.Count>=MaximumClients){peer.Dispose();continue;}
            connections.Add(new(peer,now));
        }
        foreach(var p in connections.ToArray())
            if(!p.Peer.IsConnected||p.RejectedUntil>0&&now>=p.RejectedUntil||!p.Active&&p.RejectedUntil==0&&now-p.Joined>10)Remove(p);
        var inputPeers=connections.ToArray();long inputStarted=Stopwatch.GetTimestamp();int inputMessages=0;
        for(int index=0;index<inputPeers.Length;index++){
            if(inputMessages>=64||Stopwatch.GetElapsedTime(inputStarted).TotalMilliseconds>=4)break;
            var p=inputPeers[(index+fairness)%inputPeers.Length];
            if(p.RejectedUntil>0)continue;
            try{
                int read=0;
                while(read++<8&&inputMessages<64&&Stopwatch.GetElapsedTime(inputStarted).TotalMilliseconds<4&&p.Peer.TryReceive(out var message)){
                    inputMessages++;
                    if(!p.Active){if(message.Type!="hello"){Reject(p,"Expected handshake.",now);break;}Hello(p,message.Read<NetHello>(),now);if(!p.Active)break;continue;}
                    if(message.Type=="pose")AcceptPose(p,message.Read<NetPose>(),now);
                    else if(message.Type=="command")Execute(p,message.Read<NetCommand>(),now);
                    else{Reject(p,"Unexpected multiplayer message.",now);break;}
                }
            }catch(Exception e)when(e is JsonException or ArgumentException or InvalidDataException or InvalidOperationException or OverflowException or KeyNotFoundException){
                Reject(p,"Invalid multiplayer request.",now);
            }
        }
        foreach(var p in connections.Where(p=>p.Active)){
            var cell=new Cell((int)MathF.Floor(p.Pose.Position.X),(int)MathF.Floor(p.Pose.Position.Y),(int)MathF.Floor(p.Pose.Position.Z));
            if(world.IsCellAvailable(cell)&&(p.NeedsSpawnResolution||CollisionSystem.Collides(world,p.Pose.Position))){
                var resolved=SafeSpawn(p.Pose,p.Id);
                if(CollisionSystem.Collides(world,resolved.Position))resolved=SafeSpawn(hostPose,p.Id);
                if(resolved!=p.Pose)p.CorrectPosition=true;p.Pose=resolved;p.NeedsSpawnResolution=false;Persist(p);
            }
        }
        if(lastCollectionTick!=world.TickCount){
            lastCollectionTick=world.TickCount;
            if(world.DroppedItems.Count>0)foreach(var p in connections.Where(p=>p.Active)){
                int collected=world.RunAsInventory(p.Bag,()=>world.CollectDrops(p.Pose.Position));if(collected>0)Persist(p);
            }
        }
        var active=connections.Where(p=>p.Active).ToArray();
        int budget=8;
        for(int i=0;i<active.Length;i++){
            var p=active[(i+fairness)%active.Length];
            try{
                int sent=SyncChunks(p,Math.Min(MaximumChunkMessagesPerClientPump,budget));budget-=sent;LastChunkMessages+=sent;
                if(now-p.LastState>=.05){SendState(p);p.LastState=now;}
            }catch(Exception e)when(e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException){Reject(p,"Could not synchronize this player.",now);}
        }
        fairness++;if(fairness>1000000)fairness=0;
    }
    void Hello(Player p,NetHello hello,double now)
    {
        if(hello is null||hello.Version!=NetworkProtocol.Version||hello.Fingerprint!=fingerprint){Reject(p,"Version or content mismatch.",now);return;}
        if(!MultiplayerContent.ValidName(hello.Name)||hello.Identity==Guid.Empty||hello.Password is null||hello.Password.Length>64){Reject(p,"Invalid player profile.",now);return;}
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)),SHA256.HashData(Encoding.UTF8.GetBytes(hello.Password)))){Reject(p,"Incorrect session password.",now);return;}
        string key=MultiplayerContent.IdentityKey(hello.Identity);
        if(connections.Any(other=>other!=p&&other.Active&&other.Key==key)){Reject(p,"This player profile is already connected.",now);return;}
        if(world.NetworkPlayers.TryGetValue(key,out var saved)){
            p.Id=saved.Id;p.Bag=PlayerInventory.Restore(world.Registries.Items,saved.Inventory);
            p.Pose=new(saved.Position,saved.Yaw,saved.Pitch,saved.Flying);
            if(!world.IsStreaming&&CollisionSystem.Collides(world,p.Pose.Position))p.Pose=hostPose;
        }else{
            if(world.NetworkPlayers.Count>=128){Reject(p,"This world has reached its saved player limit.",now);return;}
            p.Id=Guid.NewGuid();
            var counts=world.Registries.Blocks.CreateInventory();
            var totals=Enumerable.Range(0,counts.Length).ToDictionary(i=>world.Registries.Blocks.GetId((Block)i).ToString(),i=>counts[i],StringComparer.Ordinal);
            p.Bag=PlayerInventory.Migrate(world.Registries.Items,totals,new BlockPaletteModel().Hotbar.Select(world.Registries.Blocks.GetId));p.Pose=SafeSpawn(hostPose,p.Id);p.NeedsSpawnResolution=world.IsStreaming&&!world.IsCellAvailable(new((int)MathF.Floor(p.Pose.Position.X),(int)MathF.Floor(p.Pose.Position.Y),(int)MathF.Floor(p.Pose.Position.Z)));
        }
        p.Key=key;p.Name=hello.Name.Trim();p.Active=true;p.LastPose=now;Persist(p);
        p.Peer.Send(NetMessage.Create("welcome",new NetWelcome(NetworkProtocol.Version,p.Id,world.Seed,world.IsStreaming,p.Pose,p.Bag.Capture())));
    }
    NetPose SafeSpawn(NetPose preferred,Guid ignorePlayer)
    {
        bool Clear(Vector3 position){
            if(!MultiplayerContent.ValidPose(preferred with{Position=position})||
                world.IsStreaming&&!world.IsCellAvailable(new((int)MathF.Floor(position.X),(int)MathF.Floor(position.Y),(int)MathF.Floor(position.Z)))||
                CollisionSystem.Collides(world,position))return false;
            if(Math.Abs(position.X-hostPose.Position.X)<.7f&&Math.Abs(position.Z-hostPose.Position.Z)<.7f&&Math.Abs(position.Y-hostPose.Position.Y)<1.75f)return false;
            return !connections.Any(p=>p.Active&&p.Id!=ignorePlayer&&Math.Abs(position.X-p.Pose.Position.X)<.7f&&Math.Abs(position.Z-p.Pose.Position.Z)<.7f&&Math.Abs(position.Y-p.Pose.Position.Y)<1.75f);
        }
        foreach(float radius in new[]{1.2f,2.2f,3.2f})
        foreach(var offset in new[]{new Vector3(radius,0,0),new Vector3(-radius,0,0),new Vector3(0,0,radius),new Vector3(0,0,-radius)})
        foreach(float y in new[]{0f,1f,2f,-1f}){
            var position=preferred.Position+offset+new Vector3(0,y,0);
            if(!Clear(position))continue;
            var below=new Cell((int)MathF.Floor(position.X),(int)MathF.Floor(position.Y-.05f),(int)MathF.Floor(position.Z));
            if(preferred.Flying||world.Solid(below))return preferred with{Position=position};
        }
        if(!CollisionSystem.Collides(world,preferred.Position))return preferred;
        foreach(float y in new[]{1f,2f,3f,4f,5f,6f,7f,8f}){
            var position=preferred.Position+new Vector3(0,y,0);
            if(Clear(position))return preferred with{Position=position};
        }
        return preferred;
    }
    void Reject(Player p,string reason,double now)
    {
        if(p.RejectedUntil>0)return;
        if(p.Active)Persist(p);
        p.Active=false;p.RejectedUntil=now+.5;
        p.Peer.Send(NetMessage.Create("failure",new NetFailure(reason)));
    }
    void Remove(Player p){if(p.Active)Persist(p);p.Peer.Dispose();connections.Remove(p);}
    void Persist(Player p)
    {
        if(!string.IsNullOrEmpty(p.Key))world.NetworkPlayers[p.Key]=new(p.Id,p.Name,p.Bag.Capture(),p.Pose.Position,p.Pose.Yaw,p.Pose.Pitch,p.Pose.Flying);
    }
    void AcceptPose(Player p,NetPose pose,double now)
    {
        if(!MultiplayerContent.ValidPose(pose))throw new InvalidDataException("Invalid player pose.");
        double elapsed=Math.Max(0,now-p.BudgetAt);p.BudgetAt=now;
        p.HorizontalBudget=Math.Min(8,p.HorizontalBudget+32*elapsed);
        p.VerticalBudget=Math.Min(40,p.VerticalBudget+90*elapsed);
        var movement=pose.Position-p.Pose.Position;
        double horizontal=Math.Sqrt(movement.X*movement.X+movement.Z*movement.Z),vertical=Math.Abs(movement.Y);
        if(horizontal>p.HorizontalBudget+.001||vertical>p.VerticalBudget+.001){p.CorrectPosition=true;return;}
        // Sweep from the last accepted position so a fast pose packet cannot tunnel through a wall.
        if(world.IsStreaming&&!world.IsCellAvailable(new((int)MathF.Floor(pose.Position.X),(int)MathF.Floor(pose.Position.Y),(int)MathF.Floor(pose.Position.Z)))){p.CorrectPosition=true;return;}
        if(CollisionSystem.Collides(world,pose.Position)){p.CorrectPosition=true;return;}
        var swept=p.Pose.Position;
        CollisionSystem.Move(world,ref swept,pose.Position-p.Pose.Position);
        if(Vector3.DistanceSquared(swept,pose.Position)>.0025f){p.CorrectPosition=true;return;}
        p.HorizontalBudget=Math.Max(0,p.HorizontalBudget-horizontal);p.VerticalBudget=Math.Max(0,p.VerticalBudget-vertical);
        p.CorrectPosition=false;
        p.Pose=pose;p.LastPose=now;Persist(p);
    }
    bool Near(Player p,Cell cell)=>world.IsCellAvailable(cell)&&cell.Y is >=0 and <=255&&
        Vector3.DistanceSquared(p.Pose.Position+new Vector3(0,1.62f,0),cell.Center)<=49;
    bool Visible(Player p,Cell cell,bool placement)
    {
        if(!Near(p,cell))return false;
        var eye=p.Pose.Position+new Vector3(0,1.62f,0);var delta=cell.Center-eye;float distance=delta.Length();
        if(distance<.01f)return placement;
        var hit=world.Raycast(eye,delta/distance,placement?Math.Max(.01f,distance-.65f):distance+.7f);
        return placement?hit is null:hit?.Hit==cell;
    }
    static bool ValidSlot(ItemSlot? slot)=>slot is {} s&&(s.Area switch{ItemArea.Storage=>s.Index is >=0 and <36,ItemArea.Craft2=>s.Index is >=0 and <4,ItemArea.Craft3=>s.Index is >=0 and <9,_=>false});
    void Execute(Player p,NetCommand command,double now)
    {
        if(command is null||command.Sequence<=0)throw new InvalidDataException("Missing command sequence.");
        if(command.Sequence<=p.Sequence){Reply(p,command.Sequence,false,"Duplicate or stale command.");return;}
        p.Sequence=command.Sequence;
        if(now-p.RateStart>=1){p.RateStart=now;p.CommandsThisSecond=0;}
        if(++p.CommandsThisSecond>100){Reply(p,command.Sequence,false,"Too many commands.");return;}
        bool accepted=false;string reason="Action rejected.";
        try{
            switch(command.Kind){
                case NetCommandKind.Place:
                    if(command.Slot is <0 or >=9||p.Bag.Slots[command.Slot] is not {} stack||
                       !world.Registries.Blocks.TryGetBlock(stack.Id,out var block)||!Visible(p,command.Cell,true)||
                       !World.Neighbors.Any(n=>command.Cell.TryOffset(n,out var neighbor)&&world.Get(neighbor)!=Block.Air)||
                       block is not(Block.Wire or Block.Lever)&&AnyPlayerOverlaps(command.Cell)||now<p.NextPlace)break;
                    accepted=world.RunAsInventory(p.Bag,()=>world.PlaceFromSlot(command.Cell,command.Slot));if(accepted)p.NextPlace=now+.10;break;
                case NetCommandKind.Mine:
                    if(!Visible(p,command.Cell,false)||now<p.NextMine)break;
                    float hardness=world.Registries.Blocks.GetInfo(world.Get(command.Cell)).Hardness;
                    accepted=world.RunAsInventory(p.Bag,()=>world.Mine(command.Cell));if(accepted)p.NextMine=now+Math.Max(.05,hardness);break;
                case NetCommandKind.Toggle:
                    if(now>=p.NextPlace&&Visible(p,command.Cell,false)){accepted=world.Toggle(command.Cell);if(accepted)p.NextPlace=now+.10;}break;
                case NetCommandKind.Click:
                    if(!ValidSlot(command.Target))break;
                    p.Bag.Click(command.Target!.Value,command.Right);accepted=true;break;
                case NetCommandKind.Shift:
                    if(!ValidSlot(command.Source))break;
                    accepted=p.Bag.Shift(command.Source!.Value)>0;break;
                case NetCommandKind.Drag:
                    if(command.Targets is null||command.Targets.Length is <1 or >49||command.Targets.Any(s=>!ValidSlot(s)))break;
                    accepted=p.Bag.Drag(command.Targets,command.Right)>0;break;
                case NetCommandKind.Assign:
                    if(command.Item is null||command.Slot is <0 or >=9)break;
                    accepted=p.Bag.AssignHotbar(new(command.Item),command.Slot);break;
                case NetCommandKind.Craft:
                    if(command.Size is not(2 or 3))break;
                    if(command.Size==3&&(command.Bench is not {} bench||world.Get(bench)!=Block.CraftingTable||!Near(p,bench)))break;
                    accepted=recipes.Craft(p.Bag,command.Size,command.ToStorage,command.Repeat,maximumIterations:64)>0;break;
                case NetCommandKind.Drop:
                    if(command.Source is not null&&!ValidSlot(command.Source))break;
                    var direction=new Vector3(MathF.Sin(p.Pose.Yaw),0,MathF.Cos(p.Pose.Yaw));
                    var drop=p.Pose.Position+direction*1.2f+new Vector3(0,.5f,0);
                    drop.Y=Math.Clamp(drop.Y,0,255);drop.X=Math.Clamp(drop.X,-World.MaximumPlayerCoordinate,World.MaximumPlayerCoordinate);drop.Z=Math.Clamp(drop.Z,-World.MaximumPlayerCoordinate,World.MaximumPlayerCoordinate);
                    accepted=world.RunAsInventory(p.Bag,()=>world.DropItem(command.Source,command.Whole?int.MaxValue:1,drop));break;
                default:throw new InvalidDataException("Unknown command.");
            }
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException){accepted=false;reason="Invalid action.";}
        if(accepted){Persist(p);reason="Accepted.";}
        Reply(p,command.Sequence,accepted,reason);
        p.LastState=-1;
    }
    void Reply(Player p,long sequence,bool accepted,string reason)
    {
        if(accepted)AcceptedCommands++;else RejectedCommands++;
        p.Peer.Send(NetMessage.Create("reply",new NetCommandResult(sequence,accepted,reason)));
    }
    static long ColumnDistance(ChunkCoord coord,ColumnCoord center)=>Math.Max(Math.Abs((long)coord.X-center.X),Math.Abs((long)coord.Z-center.Z));
    bool InRange(Player p,Cell cell)=>ColumnDistance(ChunkCoord.FromWorld(cell),ColumnCoord.FromPosition(p.Pose.Position))<=ReplicationRadius;
    Stamp CurrentStamp(ChunkCoord coord,Chunk chunk)=>new(chunk,world.Chunks.GetContentRevision(coord),ChunkSaveStore.SwitchSignature(world,coord));
    bool SentCurrent(Player p,ChunkCoord coord,Chunk chunk)=>p.Sent.TryGetValue(coord,out var stamp)&&ReferenceEquals(stamp.Chunk,chunk)&&stamp.ContentRevision==world.Chunks.GetContentRevision(coord)&&stamp.Switches==ChunkSaveStore.SwitchSignature(world,coord);
    int SyncChunks(Player p,int maximum)
    {
        int sent=0;var center=ColumnCoord.FromPosition(p.Pose.Position);
        foreach(var coord in p.Sent.Keys.Where(c=>ColumnDistance(c,center)>ReplicationRadius||!world.Chunks.Chunks.ContainsKey(c)).ToArray()){
            if(sent==maximum)return sent;
            if(!p.Peer.Send(NetMessage.Create("chunk",new NetChunk(coord,null))))return sent;
            p.Sent.Remove(coord);sent++;TotalChunkMessages++;
        }
        foreach(var pair in world.Chunks.Chunks.Where(kv=>kv.Key.Y is >=0 and <=15&&ColumnDistance(kv.Key,center)<=ReplicationRadius).OrderBy(kv=>ColumnDistance(kv.Key,center)).ThenBy(kv=>Math.Abs(kv.Key.Y-(int)(p.Pose.Position.Y/16)))){
            if(sent==maximum)break;
            if(SentCurrent(p,pair.Key,pair.Value))continue;
            var data=ChunkSaveStore.CaptureChunk(world,pair.Key);
            if(!p.Peer.Send(NetMessage.Create("chunk",new NetChunk(pair.Key,data))))break;
            p.Sent[pair.Key]=CurrentStamp(pair.Key,pair.Value);sent++;TotalChunkMessages++;
        }
        return sent;
    }
    RemotePlayerState[] Roster()=>new[]{new RemotePlayerState(LocalPlayerId,hostName,hostPose.Position,hostPose.Yaw,hostPose.Pitch,hostPose.Flying)}
        .Concat(connections.Where(p=>p.Active).Select(p=>new RemotePlayerState(p.Id,p.Name,p.Pose.Position,p.Pose.Yaw,p.Pose.Pitch,p.Pose.Flying))).ToArray();
    void SendState(Player p)
    {
        var center=ColumnCoord.FromPosition(p.Pose.Position);
        var available=(world.IsStreaming?world.AvailableColumns:world.Chunks.Chunks.Keys.Select(ColumnCoord.FromChunk).Append(center))
            .Where(c=>Math.Max(Math.Abs((long)c.X-center.X),Math.Abs((long)c.Z-center.Z))<=ReplicationRadius).Distinct()
            .Where(c=>world.Chunks.Chunks.Where(kv=>ColumnCoord.FromChunk(kv.Key)==c&&kv.Key.Y is >=0 and <=15).All(kv=>SentCurrent(p,kv.Key,kv.Value))).ToArray();
        var ready=available.Contains(center);
        var falling=new FallingBlockSaveState(world.FallingBlocks.Active.Where(a=>InRange(p,new((int)MathF.Floor(a.Position.X),(int)MathF.Floor(a.Position.Y),(int)MathF.Floor(a.Position.Z))))
            .Select(a=>new FallingBlockState(a.Id,a.PersistentId.ToString(),a.Position,a.PreviousPosition,a.VelocityY,a.Data?.Clone())).ToArray(),[]);
        var power=world.Power.Where(kv=>InRange(p,kv.Key)).Select(kv=>new NetPower(kv.Key,kv.Value)).ToArray();
        var drops=world.DroppedItems.Where(d=>InRange(p,new((int)MathF.Floor(d.Position.X),(int)MathF.Floor(d.Position.Y),(int)MathF.Floor(d.Position.Z)))).ToArray();
        if(p.Peer.Send(NetMessage.Create("state",new NetState(world.TickCount,p.Sequence,ready,p.CorrectPosition,Roster(),p.Bag.Capture(),falling,drops,power,available)))){TotalStateMessages++;p.CorrectPosition=false;}
    }
    public void Dispose()
    {
        if(disposed)return;CheckOwner();disposed=true;
        foreach(var p in connections.ToArray())Remove(p);listener.Dispose();world.ClearHistory();
    }
}

public sealed class MultiplayerClient:IDisposable
{
    readonly NetPeer peer;
    readonly GameRegistries registries;
    readonly int owner=Environment.CurrentManagedThreadId;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly SortedSet<long> pending=[];
    long lastSent;
    double lastPoseSent=-1;
    bool disposed;
    NetPose? positionCorrection;
    public World? World {get;private set;}
    public bool Ready {get;private set;}
    public string Status {get;private set;}="Connecting...";
    public string? Error {get;private set;}
    public Guid LocalPlayerId {get;private set;}
    public NetPose? LocalPose {get;private set;}
    public IReadOnlyList<RemotePlayerState> Players {get;private set;}=Array.Empty<RemotePlayerState>();
    public NetCommandResult? LastReply {get;private set;}
    public long LastAcknowledgedSequence {get;private set;}
    public int PendingCommands=>pending.Count;
    public bool IsConnected=>!disposed&&peer.IsConnected&&Error is null;
    public MultiplayerClient(NetPeer peer,string name,string password,Guid identity,GameRegistries? registries=null)
    {
        this.peer=peer??throw new ArgumentNullException(nameof(peer));this.registries=registries??GameRegistries.Default;
        if(!MultiplayerContent.ValidName(name)||password is null||password.Length>64||identity==Guid.Empty)throw new ArgumentException("Invalid player profile.");
        if(!peer.Send(NetMessage.Create("hello",new NetHello(NetworkProtocol.Version,MultiplayerContent.Fingerprint(this.registries),name.Trim(),password,identity))))Fail("Could not send handshake.");
    }
    void CheckOwner(){if(Environment.CurrentManagedThreadId!=owner)throw new InvalidOperationException("Multiplayer sessions belong to their owner thread.");}
    public void Pump(NetPose pose)
    {
        CheckOwner();if(disposed||Error is not null)return;
        bool welcomed=false;
        try{
            int read=0;
            while(read++<64&&peer.TryReceive(out var message)){
                switch(message.Type){
                    case "welcome":
                        if(World is not null)throw new InvalidDataException("Repeated welcome.");
                        var welcome=message.Read<NetWelcome>();
                        if(welcome.Version!=NetworkProtocol.Version||welcome.PlayerId==Guid.Empty||!MultiplayerContent.ValidPose(welcome.Spawn))throw new InvalidDataException("Invalid welcome.");
                        var candidate=BlockCircuit.World.CreateNetworkReplica(welcome.Seed,welcome.Streaming,registries);
                        candidate.ApplyNetworkInventory(welcome.Inventory);
                        World=candidate;LocalPlayerId=welcome.PlayerId;LocalPose=welcome.Spawn;welcomed=true;Status="Loading shared terrain...";break;
                    case "chunk":
                        if(World is null)throw new InvalidDataException("Chunk before welcome.");
                        var chunk=message.Read<NetChunk>();World.ApplyNetworkChunk(chunk.Coord,chunk.Data);break;
                    case "state":
                        ApplyState(message.Read<NetState>());break;
                    case "reply":
                        var reply=message.Read<NetCommandResult>();
                        if(reply.Sequence<=0||reply.Sequence>lastSent||reply.Reason is null||reply.Reason.Length>128)throw new InvalidDataException("Invalid command reply.");
                        LastReply=reply;break;
                    case "failure":Fail(message.Read<NetFailure>().Reason);return;
                    default:throw new InvalidDataException("Unexpected host message.");
                }
            }
            if(!peer.IsConnected){Fail(peer.Error?.Message??"Disconnected from host.");return;}
            if(World is null&&clock.Elapsed.TotalSeconds>12){Fail("Handshake timed out.");return;}
            if(Ready&&!welcomed&&clock.Elapsed.TotalSeconds-lastPoseSent>=.05&&MultiplayerContent.ValidPose(pose)){
                peer.Send(NetMessage.Create("pose",pose));lastPoseSent=clock.Elapsed.TotalSeconds;
            }
        }catch(Exception e)when(e is JsonException or ArgumentException or InvalidDataException or InvalidOperationException or OverflowException or KeyNotFoundException){
            Fail("Invalid host data: "+e.Message);
        }
    }
    void ApplyState(NetState state)
    {
        if(World is null||state is null||state.Inventory is null||state.Tick<0||state.AckSequence<0||state.AckSequence>lastSent||state.Players is null||state.Players.Length is <1 or >9||
            state.Players.Any(p=>p is null||p.Id==Guid.Empty||!MultiplayerContent.ValidName(p.Name)||!MultiplayerContent.ValidPose(new(p.Position,p.Yaw,p.Pitch,p.Flying)))||
            state.Players.Select(p=>p.Id).Distinct().Count()!=state.Players.Length||state.Power is null||state.Power.Length>250000||state.Available is null||
            state.Drops is null||state.Falling is null)throw new InvalidDataException("Invalid host state.");
        var self=state.Players.SingleOrDefault(p=>p.Id==LocalPlayerId)??throw new InvalidDataException("Missing local player.");
        if(state.Tick<World.TickCount)return;
        if(state.AckSequence>=lastSent)World.ApplyNetworkInventory(state.Inventory);
        LastAcknowledgedSequence=Math.Max(LastAcknowledgedSequence,state.AckSequence);pending.RemoveWhere(s=>s<=LastAcknowledgedSequence);
        var power=new Dictionary<Cell,int>();
        foreach(var cell in state.Power)if(cell is null||!power.TryAdd(cell.Cell,cell.Level))throw new InvalidDataException("Duplicate circuit power cell.");
        World.ApplyNetworkMetadata(state.Tick,state.Falling,state.Drops,power,state.Available);
        Players=Array.AsReadOnly(state.Players);LocalPose=new(self.Position,self.Yaw,self.Pitch,self.Flying);
        if(state.CorrectPosition)positionCorrection=LocalPose;
        Ready=state.TerrainReady;Status=Ready?$"Connected / {Players.Count} players":"Loading shared terrain...";
    }
    public NetPose? ConsumePositionCorrection(){CheckOwner();var pose=positionCorrection;positionCorrection=null;return pose;}
    public long Request(NetCommand command)
    {
        CheckOwner();
        if(disposed||!Ready||Error is not null||!peer.IsConnected||command is null)return 0;
        long sequence=command.Sequence>0?command.Sequence:checked(lastSent+1);
        lastSent=Math.Max(lastSent,sequence);
        if(!peer.Send(NetMessage.Create("command",command with{Sequence=sequence})))return 0;
        pending.Add(sequence);return sequence;
    }
    void Fail(string reason){Ready=false;Error=string.IsNullOrWhiteSpace(reason)?"Disconnected from host.":reason[..Math.Min(reason.Length,256)];Status=Error;peer.Dispose();}
    public void Dispose(){if(disposed)return;CheckOwner();disposed=true;Ready=false;pending.Clear();peer.Dispose();Status="Disconnected";}
}
