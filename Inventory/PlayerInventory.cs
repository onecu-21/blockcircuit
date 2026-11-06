namespace BlockCircuit;

public sealed record ItemStack(ContentId Id, int Count);
public enum ItemArea { Storage, Craft2, Craft3 }
public readonly record struct ItemSlot(ItemArea Area, int Index);
public sealed record StackData(string Id, int Count);
public sealed record PlayerInventoryState(StackData?[]? Slots, StackData?[]? Craft2, StackData?[]? Craft3,
    StackData? Cursor, Dictionary<string,int>? Reserve);

/// <summary>Owner-thread inventory. Slots 0..8 are the hotbar. Reserve is lossless legacy overflow.</summary>
public sealed class PlayerInventory
{
    readonly ItemRegistry registry;
    readonly ItemStack?[] slots=new ItemStack?[36], craft2=new ItemStack?[4], craft3=new ItemStack?[9];
    readonly Dictionary<string,int> reserve=new(StringComparer.Ordinal);
    public IReadOnlyList<ItemStack?> Slots { get; }
    public IReadOnlyList<ItemStack?> Crafting2 { get; }
    public IReadOnlyList<ItemStack?> Crafting3 { get; }
    public IReadOnlyDictionary<string,int> Reserve => new System.Collections.ObjectModel.ReadOnlyDictionary<string,int>(reserve);
    public ItemStack? Cursor { get; private set; }
    public event Action? Changed;
    public PlayerInventory(ItemRegistry registry)
    {
        if(!registry.IsFrozen)throw new ArgumentException("Freeze items before creating an inventory.");
        this.registry=registry;
        Slots=Array.AsReadOnly(slots);Crafting2=Array.AsReadOnly(craft2);Crafting3=Array.AsReadOnly(craft3);
    }
    int Limit(ContentId id)=>registry.TryGetRuntimeId(id,out int runtime)?registry.Get(runtime).MaxStack:1024;
    public bool IsKnown(ContentId id)=>registry.TryGetRuntimeId(id,out _);
    ItemStack?[] ArrayFor(ItemArea area)=>area switch{ItemArea.Storage=>slots,ItemArea.Craft2=>craft2,ItemArea.Craft3=>craft3,_=>throw new ArgumentOutOfRangeException(nameof(area))};
    public ItemStack? Get(ItemSlot slot)=>ArrayFor(slot.Area)[slot.Index];
    internal void Set(ItemSlot slot,ItemStack? stack)=>ArrayFor(slot.Area)[slot.Index]=stack;
    static ItemStack? Less(ItemStack stack,int amount)=>stack.Count==amount?null:stack with{Count=stack.Count-amount};
    void Notify()=>Changed?.Invoke();
    public Dictionary<string,int> Totals()
    {
        var result=new Dictionary<string,int>(reserve,StringComparer.Ordinal);
        foreach(var stack in slots.Concat(craft2).Concat(craft3).Append(Cursor))
            if(stack is not null)result[stack.Id.ToString()]=checked(result.GetValueOrDefault(stack.Id.ToString())+stack.Count);
        foreach(var key in result.Where(p=>p.Value==0).Select(p=>p.Key).ToArray())result.Remove(key);
        return result;
    }
    public static PlayerInventory Migrate(ItemRegistry registry,IReadOnlyDictionary<string,int> counts,IEnumerable<ContentId>? hotbar=null)
    {
        var bag=new PlayerInventory(registry);
        foreach(var pair in counts){_=new ContentId(pair.Key);if(pair.Value<0)throw new InvalidDataException("Negative item count.");if(pair.Value>0)bag.reserve.Add(pair.Key,pair.Value);}
        int slot=0;
        foreach(var id in hotbar??[]){
            if(slot==9)break;bag.Withdraw(id,slot++ ,notify:false);
        }
        foreach(string id in hotbar is null ? bag.reserve.Keys.Order(StringComparer.Ordinal).ToArray() : System.Array.Empty<string>()){
            for(int i=0;i<36&&bag.reserve.GetValueOrDefault(id)>0;i++)if(bag.slots[i] is null)bag.Withdraw(new(id),i,notify:false);
        }
        return bag;
    }
    public bool Withdraw(ContentId id,int destination,bool notify=true)
    {
        if((uint)destination>=36||!IsKnown(id))return false;
        int available=reserve.GetValueOrDefault(id.ToString());
        var target=slots[destination];
        if(available==0||target is not null&&target.Id!=id)return false;
        int moved=Math.Min(available,Limit(id)-(target?.Count??0));
        if(moved<=0)return false;
        slots[destination]=new(id,(target?.Count??0)+moved);
        if(available==moved)reserve.Remove(id.ToString());else reserve[id.ToString()]=available-moved;
        if(notify)Notify();return true;
    }
    public bool AssignHotbar(ContentId id,int destination)
    {
        if((uint)destination>=9||!IsKnown(id))return false;
        int source=System.Array.FindIndex(slots,s=>s?.Id==id);
        if(source>=0){(slots[source],slots[destination])=(slots[destination],slots[source]);Notify();return true;}
        if(reserve.GetValueOrDefault(id.ToString())==0)return false;
        if(slots[destination] is {} old){
            int free=System.Array.FindIndex(slots,s=>s is null);
            if(free<0)return false;
            slots[free]=old;slots[destination]=null;
        }
        return Withdraw(id,destination);
    }
    public bool TryAdd(ItemStack stack)
    {
        if(stack.Count<=0||!IsKnown(stack.Id))return false;
        if((long)Totals().GetValueOrDefault(stack.Id.ToString())+stack.Count>int.MaxValue)return false;
        long capacity=slots.Where(s=>s is null||s.Id==stack.Id).Sum(s=>(long)Limit(stack.Id)-(s?.Count??0));
        if(capacity<stack.Count)return false;
        int remaining=stack.Count;
        for(int pass=0;pass<2&&remaining>0;pass++)for(int i=0;i<36&&remaining>0;i++){
            var old=slots[i];
            if(pass==0?(old is null||old.Id!=stack.Id):old is not null)continue;
            int moved=Math.Min(remaining,Limit(stack.Id)-(old?.Count??0));
            if(moved>0){slots[i]=new(stack.Id,(old?.Count??0)+moved);remaining-=moved;}
        }
        Notify();return true;
    }
    public bool CanAdd(ItemStack stack)=>Restore(registry,Capture()).TryAdd(stack);
    public int Available(ContentId id)=>slots.Where(s=>s?.Id==id).Sum(s=>s!.Count);
    public bool TryRemove(ContentId id,int count,int onlySlot=-1)
    {
        if(count<=0||!IsKnown(id))return false;
        if(onlySlot>=0){
            if((uint)onlySlot>=36||slots[onlySlot] is not {} stack||stack.Id!=id||stack.Count<count)return false;
            slots[onlySlot]=Less(stack,count);
        }else{
            if(Available(id)<count)return false;
            for(int i=0;i<36&&count>0;i++)if(slots[i] is {} stack&&stack.Id==id){int n=Math.Min(count,stack.Count);slots[i]=Less(stack,n);count-=n;}
        }
        Notify();return true;
    }
    internal void Reclaim(ContentId id,int count)
    {
        if(TryAdd(new(id,count)))return;
        reserve[id.ToString()]=checked(reserve.GetValueOrDefault(id.ToString())+count);Notify();
    }
    public void Click(ItemSlot target,bool right=false)
    {
        var stack=Get(target);
        if(Cursor is null){
            if(stack is null)return;
            int n=right?(stack.Count+1)/2:stack.Count;
            Cursor=new(stack.Id,n);Set(target,Less(stack,n));Notify();return;
        }
        if(stack is null||stack.Id==Cursor.Id){
            if(!IsKnown(Cursor.Id))return;
            int n=Math.Min(right?1:Cursor.Count,Limit(Cursor.Id)-(stack?.Count??0));
            if(n==0)return;
            Set(target,new(Cursor.Id,(stack?.Count??0)+n));Cursor=Less(Cursor,n);
        }else{Set(target,Cursor);Cursor=stack;}
        Notify();
    }
    public int Transfer(ItemSlot from,ItemSlot to,int count=int.MaxValue)
    {
        if(from==to||count<=0||Get(from) is not {} source||!IsKnown(source.Id))return 0;
        var target=Get(to);if(target is not null&&target.Id!=source.Id)return 0;
        int n=Math.Min(Math.Min(source.Count,count),Limit(source.Id)-(target?.Count??0));
        if(n<=0)return 0;
        Set(to,new(source.Id,(target?.Count??0)+n));Set(from,Less(source,n));Notify();return n;
    }
    public int Shift(ItemSlot from)
    {
        if(Get(from) is not {} source)return 0;
        var targets=from.Area==ItemArea.Storage
            ? Enumerable.Range(from.Index<9?9:0,from.Index<9?27:9)
            : Enumerable.Range(0,36);
        int moved=0;
        for(int pass=0;pass<2;pass++)foreach(int i in targets){
            var target=slots[i];if(pass==0?target is null:target is not null)continue;
            moved+=Transfer(from,new(ItemArea.Storage,i));
        }
        return moved;
    }
    public int Drag(IEnumerable<ItemSlot> destinations,bool oneEach)
    {
        var targets=destinations.Distinct().Where(t=>Get(t) is not {} s||Cursor is {} c&&s.Id==c.Id&&s.Count<Limit(c.Id)).ToArray();
        if(Cursor is null||targets.Length==0||!IsKnown(Cursor.Id))return 0;
        int per=oneEach?1:Cursor.Count/targets.Length;
        if(per==0)return 0;
        int moved=0;
        foreach(var target in targets){
            if(Cursor is null)break;
            var old=Get(target);int n=Math.Min(Math.Min(per,Cursor.Count),Limit(Cursor.Id)-(old?.Count??0));
            if(n<=0)continue;
            Set(target,new(Cursor.Id,(old?.Count??0)+n));Cursor=Less(Cursor,n);moved+=n;
        }
        if(moved>0)Notify();return moved;
    }
    public ItemStack? Take(ItemSlot? from,int count)
    {
        var stack=from is {} slot?Get(slot):Cursor;
        if(stack is null||count<=0||!IsKnown(stack.Id))return null;
        int n=Math.Min(count,stack.Count);
        if(from is {} source)Set(source,Less(stack,n));else Cursor=Less(stack,n);
        Notify();return new(stack.Id,n);
    }
    internal bool PutCraftResult(ItemStack stack,bool toStorage)
    {
        if(toStorage)return TryAdd(stack);
        if(!IsKnown(stack.Id)||Cursor is not null&&Cursor.Id!=stack.Id||(Cursor?.Count??0)+stack.Count>Limit(stack.Id))return false;
        if((long)Totals().GetValueOrDefault(stack.Id.ToString())+stack.Count>int.MaxValue)return false;
        Cursor=new(stack.Id,(Cursor?.Count??0)+stack.Count);return true;
    }
    internal void CommitCraft(PlayerInventory candidate){CopyFrom(candidate.Capture());}
    public PlayerInventoryState Capture()=>new(slots.Select(Data).ToArray(),craft2.Select(Data).ToArray(),craft3.Select(Data).ToArray(),Data(Cursor),new(reserve,StringComparer.Ordinal));
    static StackData? Data(ItemStack? stack)=>stack is null?null:new(stack.Id.ToString(),stack.Count);
    public bool Matches(PlayerInventoryState state)=>GameJson.Serialize(Capture())==GameJson.Serialize(state);
    internal void CopyFrom(PlayerInventoryState state)
    {
        var other=Restore(registry,state);
        System.Array.Copy(other.slots,slots,36);System.Array.Copy(other.craft2,craft2,4);System.Array.Copy(other.craft3,craft3,9);
        Cursor=other.Cursor;reserve.Clear();foreach(var p in other.reserve)reserve.Add(p.Key,p.Value);Notify();
    }
    public static PlayerInventory Restore(ItemRegistry registry,PlayerInventoryState state)
    {
        if(state is null||state.Slots?.Length!=36||state.Craft2?.Length!=4||state.Craft3?.Length!=9||state.Reserve is null||state.Reserve.Count>100000)throw new InvalidDataException("Invalid inventory dimensions.");
        var bag=new PlayerInventory(registry);
        ItemStack? Stack(StackData? data){
            if(data is null)return null;
            var id=new ContentId(data.Id);
            if(data.Count<=0||data.Count>bag.Limit(id))throw new InvalidDataException("Invalid stack size.");
            return new(id,data.Count);
        }
        for(int i=0;i<36;i++)bag.slots[i]=Stack(state.Slots[i]);
        for(int i=0;i<4;i++)bag.craft2[i]=Stack(state.Craft2[i]);
        for(int i=0;i<9;i++)bag.craft3[i]=Stack(state.Craft3[i]);
        bag.Cursor=Stack(state.Cursor);
        foreach(var pair in state.Reserve){_=new ContentId(pair.Key);if(pair.Value<=0)throw new InvalidDataException("Invalid reserve count.");bag.reserve.Add(pair.Key,pair.Value);}
        _=bag.Totals();return bag;
    }
}
