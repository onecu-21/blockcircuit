namespace BlockCircuit;

public static class InventoryTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);Console.WriteLine("PASS: "+name);checks++;}
        var registry=GameRegistries.Default.Items;
        var stone=LegacyBlockIds.GetPersistentId((int)Block.Stone);
        var dirt=LegacyBlockIds.GetPersistentId((int)Block.Dirt);
        var bag=new PlayerInventory(registry);
        Check(bag.Slots.Count==36&&bag.Crafting2.Count==4&&bag.Crafting3.Count==9,"36 slots include nine hotbar slots, with separate 2x2 and 3x3 inputs");
        Check(bag.TryAdd(new(stone,128))&&bag.Slots[0]?.Count==64&&bag.Slots[1]?.Count==64,"collection obeys stack limits and splits across slots");
        bag.Click(new(ItemArea.Storage,0),true);
        Check(bag.Cursor?.Count==32&&bag.Slots[0]?.Count==32,"right click takes half of a stack");
        bag.Click(new(ItemArea.Storage,2),true);
        Check(bag.Cursor?.Count==31&&bag.Slots[2]?.Count==1,"right click places one item");
        bag.Click(new(ItemArea.Storage,0));
        Check(bag.Cursor is null&&bag.Slots[0]?.Count==63,"left click merges into a compatible stack");
        Check(bag.Shift(new(ItemArea.Storage,0))==63&&bag.Slots[0] is null&&bag.Slots[9]?.Count==63,"shift click moves hotbar items into main storage");
        bag.Click(new(ItemArea.Storage,1));
        bag.Drag([new(ItemArea.Craft2,0),new(ItemArea.Craft2,1),new(ItemArea.Craft2,0)],false);
        Check(bag.Crafting2[0]?.Count==32&&bag.Crafting2[1]?.Count==32&&bag.Cursor is null,"drag evenly distributes once per unique destination");
        var totals=bag.Totals();
        var random=new Random(51987);
        for(int i=0;i<10000;i++){
            var slot=new ItemSlot((ItemArea)random.Next(3),random.Next(4));
            if(random.Next(3)==0)bag.Shift(slot);else bag.Click(slot,random.Next(2)==0);
        }
        Check(Equal(totals,bag.Totals()),"10000 seeded clicks and transfers conserve every item");
        var full=new PlayerInventory(registry);
        Check(full.TryAdd(new(stone,36*64))&&!full.TryAdd(new(dirt,1))&&full.Totals()[stone.ToString()]==2304,"full inventory rejects collection atomically");
        var migrated=PlayerInventory.Migrate(registry,new Dictionary<string,int>{{stone.ToString(),int.MaxValue},{dirt.ToString(),128}},[stone,dirt]);
        Check(migrated.Slots[0]?.Id==stone&&migrated.Slots[1]?.Id==dirt&&migrated.Totals()[stone.ToString()]==int.MaxValue,"legacy migration prioritizes hotbar and preserves overflow without truncation");
        var before=migrated.Capture();
        migrated.Click(new(ItemArea.Storage,0));
        migrated.Click(new(ItemArea.Craft3,8),true);
        var restored=PlayerInventory.Restore(registry,migrated.Capture());
        Check(restored.Matches(migrated.Capture()),"cursor, both crafting grids, reserve and slots round trip exactly");
        var absent=GameRegistries.Create(r=>r.Items.Register(new("example:single"),new("Single",1)));
        var custom=new PlayerInventory(absent.Items);
        Check(custom.TryAdd(new(new("example:single"),2))&&custom.Slots[0]?.Count==1&&custom.Slots[1]?.Count==1,"registered custom maximum stack sizes are enforced");
        Check(Rejected(()=>PlayerInventory.Restore(registry,before with{Slots=[]})),"malformed slot array is rejected");
        var changed=before.Slots!.ToArray();changed[0]=new(stone.ToString(),65);
        Check(Rejected(()=>PlayerInventory.Restore(registry,before with{Slots=changed})),"overstacked known items are rejected");
        Check(Rejected(()=>GameRegistries.Create(r=>r.Items.Register(new("example:bad"),new("Bad",0)))),"invalid registered stack limits are rejected");
        Check(!bag.TryRemove(stone,int.MaxValue)&&Equal(totals,bag.Totals()),"failed removal does not consume partial inputs");
        Console.WriteLine($"Inventory tests passed: {checks}");
    }
    internal static bool Equal(IReadOnlyDictionary<string,int> a,IReadOnlyDictionary<string,int> b)=>a.Count==b.Count&&a.All(p=>b.GetValueOrDefault(p.Key)==p.Value);
    internal static bool Rejected(Action action){try{action();return false;}catch(Exception e)when(e is ArgumentException or InvalidDataException or InvalidOperationException){return true;}}
}
