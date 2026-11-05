using System.Numerics;
namespace BlockCircuit;

public sealed record DroppedItemData(Guid Id,string Item,int Count,Vector3 Position,long AvailableAt);
public sealed record InventorySaveState(PlayerInventoryState Player,DroppedItemData[]? Drops);

public sealed partial class World
{
    public PlayerInventory? Items { get; private set; }
    readonly List<DroppedItemData> droppedItems=[];
    public IReadOnlyList<DroppedItemData> DroppedItems=>droppedItems.AsReadOnly();
    int debitSlot=-1;
    public void EnableSlotInventory(IEnumerable<Block>? preferredHotbar=null)
    {
        if(Items is not null)return;
        var counts=LegacyCounts();
        var preferred=(preferredHotbar??new BlockPaletteModel().Hotbar).Where(Registries.Blocks.IsValid).Select(Registries.Blocks.GetId);
        AttachInventory(PlayerInventory.Migrate(Registries.Items,counts,preferred));
        ClearHistory();
    }
    Dictionary<string,int> LegacyCounts(){
        var counts=new Dictionary<string,int>(unknownInventory,StringComparer.Ordinal);
        for(int i=0;i<Inventory.Length;i++)counts[Registries.Blocks.GetId((Block)i).ToString()]=Inventory[i];
        return counts;
    }
    void AttachInventory(PlayerInventory? bag)
    {
        if(Items is not null)Items.Changed-=InventoryChanged;
        Items=bag;
        if(bag is not null){bag.Changed+=InventoryChanged;InventoryChanged();}
    }
    void InventoryChanged()
    {
        if(Items is null)return;
        var counts=Items.Totals();System.Array.Clear(Inventory);unknownInventory.Clear();
        foreach(var pair in counts)
            if(Registries.Blocks.TryGetBlock(new(pair.Key),out var block))Inventory[(int)block]=pair.Value;
            else unknownInventory.Add(pair.Key,pair.Value);
        if(currentEdit is null&&!replayingHistory)ClearHistory();
    }
    internal InventorySaveState? CaptureInventory()
    {
        if(Items is null)return null;
        var state=new InventorySaveState(Items.Capture(),droppedItems.ToArray());
        ValidateInventory(state,LegacyCounts(),Registries);
        return state;
    }
    internal static void ValidateInventory(InventorySaveState? state,IReadOnlyDictionary<string,int> counts,GameRegistries registries)
    {
        if(state is null)return;
        var bag=PlayerInventory.Restore(registries.Items,state.Player);
        var nonzero=counts.Where(p=>p.Value!=0).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        if(!SameItemCounts(bag.Totals(),nonzero)||state.Drops is null||state.Drops.Length>4096)throw new InvalidDataException("Inventory counts do not match slots.");
        var ids=new HashSet<Guid>();
        foreach(var drop in state.Drops){
            if(drop is null||drop.Id==Guid.Empty||!ids.Add(drop.Id)||drop.Count<=0||drop.AvailableAt<0||
                !float.IsFinite(drop.Position.X)||!float.IsFinite(drop.Position.Y)||!float.IsFinite(drop.Position.Z)||
                Math.Abs(drop.Position.X)>MaximumPlayerCoordinate||Math.Abs(drop.Position.Z)>MaximumPlayerCoordinate||drop.Position.Y is <0 or >256)
                throw new InvalidDataException("Invalid dropped item.");
            var id=new ContentId(drop.Item);
            int max=registries.Items.TryGetRuntimeId(id,out int runtime)?registries.Items.Get(runtime).MaxStack:1024;
            if(drop.Count>max)throw new InvalidDataException("Overstacked dropped item.");
        }
    }
    static bool SameItemCounts(IReadOnlyDictionary<string,int> a,IReadOnlyDictionary<string,int> b)=>a.Count==b.Count&&a.All(p=>b.GetValueOrDefault(p.Key)==p.Value);
    internal void RestoreInventory(InventorySaveState? state)
    {
        var bag=state is null?null:PlayerInventory.Restore(Registries.Items,state.Player);
        droppedItems.Clear();if(state is not null)droppedItems.AddRange(state.Drops!);
        AttachInventory(bag);
    }
    bool CanCollectMining(Cell cell)
    {
        if(Items is null)return true;
        var copy=PlayerInventory.Restore(Registries.Items,Items.Capture());
        bool Add(Block block)=>copy.TryAdd(new(Registries.Blocks.GetId(block),1));
        if(!Add(Get(cell)))return false;
        while(cell.TryOffset(new(0,1,0),out cell)&&Get(cell) is Block.Wire or Block.Lever){
            if(!Add(Get(cell)))return false;
        }
        return true;
    }
    public bool PlaceFromSlot(Cell cell,int slot)
    {
        if(Items is null||(uint)slot>=9||Items.Slots[slot] is not {} stack||!Registries.Blocks.TryGetBlock(stack.Id,out var block))return false;
        debitSlot=slot;
        try{return Place(cell,block);}finally{debitSlot=-1;}
    }
    public bool DropItem(ItemSlot? source,int count,Vector3 position)
    {
        if(Items is null||TickCount>long.MaxValue-20||droppedItems.Count>=4096||!float.IsFinite(position.X)||!float.IsFinite(position.Y)||!float.IsFinite(position.Z)||
           Math.Abs(position.X)>MaximumPlayerCoordinate||Math.Abs(position.Z)>MaximumPlayerCoordinate||position.Y is <0 or >256)return false;
        if(Items.Take(source,count) is not {} stack)return false;
        droppedItems.Add(new(Guid.NewGuid(),stack.Id.ToString(),stack.Count,position,checked(TickCount+20)));
        ClearHistory();return true;
    }
    public int CollectDrops(Vector3 position)
    {
        if(Items is null)return 0;
        int count=0;
        for(int i=droppedItems.Count-1;i>=0;i--){
            var drop=droppedItems[i];
            if(drop.AvailableAt>TickCount||Vector3.DistanceSquared(drop.Position,position)>2.25f||
                IsStreaming&&!IsCellAvailable(new((int)MathF.Floor(drop.Position.X),(int)MathF.Floor(drop.Position.Y),(int)MathF.Floor(drop.Position.Z))))continue;
            if(!Items.TryAdd(new(new(drop.Item),drop.Count)))continue;
            droppedItems.RemoveAt(i);count+=drop.Count;
        }
        return count;
    }
}
