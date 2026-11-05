using System.Numerics;
namespace BlockCircuit;

public sealed record NetworkPlayerSaveData(Guid Id,string Name,PlayerInventoryState Inventory,Vector3 Position,float Yaw,float Pitch,bool Flying=false);

public sealed partial class World
{
    public bool IsNetworkReplica { get; private set; }
    public Dictionary<string,NetworkPlayerSaveData> NetworkPlayers { get; }=new(StringComparer.Ordinal);

    public T RunAsInventory<T>(PlayerInventory inventory,Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(action);
        if(currentEdit is not null||replayingHistory)throw new InvalidOperationException("Finish the current edit before changing player context.");
        var original=Items;var legacy=original is null?Inventory.ToArray():null;var unknown=original is null?new Dictionary<string,int>(unknownInventory):null;
        AttachInventory(inventory);
        try{return action();}
        finally{
            AttachInventory(original);
            if(original is null){Inventory=legacy!;unknownInventory.Clear();foreach(var p in unknown!)unknownInventory.Add(p.Key,p.Value);}
            ClearHistory();
        }
    }
    public void RunAsInventory(PlayerInventory inventory,Action action)=>RunAsInventory(inventory,()=>{action();return true;});
    public static World CreateNetworkReplica(int seed,bool streaming,GameRegistries registries)
    {
        var result=new World(false,seed,legacyTerrain:!streaming,registries:registries);
        result.IsNetworkReplica=true;result.SetStreamingState(streaming,StreamingTerrainGenerator.Version);
        result.EnableSlotInventory();return result;
    }
    public void ApplyNetworkInventory(PlayerInventoryState state)
    {
        _=PlayerInventory.Restore(Registries.Items,state);
        if(Items is null)AttachInventory(PlayerInventory.Restore(Registries.Items,state));else Items.CopyFrom(state);
    }
    public void ApplyNetworkChunk(ChunkCoord coord,ChunkFileData? data)
    {
        if(!IsNetworkReplica)throw new InvalidOperationException("Only a network replica accepts remote chunks.");
        if(!coord.IsValid||data is not null&&data.Coord!=coord)throw new InvalidDataException("Invalid network chunk coordinates.");
        var decoded=data is null?null:StreamingChunkData.Decode(Registries,data);
        Chunks.Remove(coord);
        if(decoded is not null){
            Chunks.Install(coord,decoded.Blocks,false);
            foreach(var p in decoded.Payloads)blockPayloads[p.Key]=p.Value;
            foreach(var cell in decoded.Switches)Switches.Add(cell);
        }
        Chunks.MarkSaved(coord,Chunks.GetContentRevision(coord));
        ClearHistory();
    }
    public void ApplyNetworkMetadata(long ticks,FallingBlockSaveState? falling,DroppedItemData[] drops,Dictionary<Cell,int> power,ColumnCoord[] available)
    {
        if(!IsNetworkReplica)throw new InvalidOperationException("Only a network replica accepts remote metadata.");
        if(ticks<0||drops is null||power is null||available is null||available.Length>8192||power.Count>250000||
            available.Distinct().Count()!=available.Length||available.Any(c=>!new ChunkCoord(c.X,0,c.Z).IsValid)||
            power.Any(p=>p.Value is <0 or >15||!ChunkCoord.FromWorld(p.Key).IsValid))throw new InvalidDataException("Invalid network metadata.");
        FallingBlockSystem.ValidateState(falling);
        ValidateInventory(new(Items!.Capture(),drops),Items.Totals(),Registries);
        TickCount=ticks;
        FallingBlocks.Restore(falling);
        droppedItems.Clear();droppedItems.AddRange(drops);
        var changed=Power.Keys.Concat(power.Keys).Distinct().Where(c=>Power.GetValueOrDefault(c)!=power.GetValueOrDefault(c)).ToArray();
        Power.Clear();foreach(var p in power)Power.Add(p.Key,p.Value);
        foreach(var cell in changed)Chunks.MarkDirty(cell);
        AvailableColumns.Clear();AvailableColumns.UnionWith(available);
        KnownColumns.Clear();KnownColumns.UnionWith(available);
    }
    internal Dictionary<string,NetworkPlayerSaveData> CaptureNetworkPlayers()
    {
        ValidateNetworkPlayers(NetworkPlayers,Registries);
        return NetworkPlayers.ToDictionary(p=>p.Key,p=>p.Value with{Inventory=PlayerInventory.Restore(Registries.Items,p.Value.Inventory).Capture()},StringComparer.Ordinal);
    }
    internal static void ValidateNetworkPlayers(Dictionary<string,NetworkPlayerSaveData>? players,GameRegistries registries)
    {
        if(players is null)return;
        if(players.Count>128)throw new InvalidDataException("Too many saved network players.");
        var publicIds=new HashSet<Guid>();
        foreach(var p in players){
            var player=p.Value;
            if(p.Key.Length!=64||p.Key.Any(c=>!Uri.IsHexDigit(c))||player is null||player.Id==Guid.Empty||!publicIds.Add(player.Id)||
                string.IsNullOrWhiteSpace(player.Name)||player.Name.Length>24||player.Name.Any(c=>c is <' ' or >'~')||
                !float.IsFinite(player.Position.X)||!float.IsFinite(player.Position.Y)||!float.IsFinite(player.Position.Z)||
                Math.Abs(player.Position.X)>MaximumPlayerCoordinate||Math.Abs(player.Position.Z)>MaximumPlayerCoordinate||player.Position.Y is <0 or >255||
                !float.IsFinite(player.Yaw)||!float.IsFinite(player.Pitch)||Math.Abs(player.Pitch)>1.5f)throw new InvalidDataException("Invalid saved network player.");
            _=PlayerInventory.Restore(registries.Items,player.Inventory);
        }
    }
    internal void RestoreNetworkPlayers(Dictionary<string,NetworkPlayerSaveData>? players)
    {
        ValidateNetworkPlayers(players,Registries);
        NetworkPlayers.Clear();
        if(players is not null)foreach(var p in players)NetworkPlayers.Add(p.Key,p.Value with{Inventory=PlayerInventory.Restore(Registries.Items,p.Value.Inventory).Capture()});
    }
}
