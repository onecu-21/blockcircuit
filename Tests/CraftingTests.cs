namespace BlockCircuit;

public static class CraftingTests
{
    public static void Run(){
        int checks=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);Console.WriteLine("PASS: "+name);checks++;}
        var r=GameRegistries.Default.Items;var book=RecipeBook.LoadDefault(r);
        var log=LegacyBlockIds.GetPersistentId((int)Block.OakLog);var planks=LegacyBlockIds.GetPersistentId((int)Block.OakPlanks);
        var bag=new PlayerInventory(r);bag.TryAdd(new(log,2));bag.Click(new(ItemArea.Storage,0));bag.Click(new(ItemArea.Craft2,3));
        Check(book.Find(bag,2)?.Result==new ItemStack(planks,4),"shapeless log recipe matches at an arbitrary 2x2 position");
        Check(book.Craft(bag,2)==4&&bag.Cursor?.Count==4&&bag.Crafting2[3]?.Count==1,"craft output and exactly one input consumption commit together");
        bag.Click(new(ItemArea.Storage,0));
        Check(book.Craft(bag,2,true,true)==4&&bag.Crafting2.All(s=>s is null),"shift crafting repeats only while ingredients remain");
        bag=new(r);bag.TryAdd(new(planks,8));bag.Click(new(ItemArea.Storage,0));
        bag.Drag(Enumerable.Range(0,4).Select(i=>new ItemSlot(ItemArea.Craft2,i)),false);
        Check(book.Find(bag,2)?.Result.Id==LegacyBlockIds.GetPersistentId((int)Block.CraftingTable),"2x2 planks match the crafting table recipe");
        Check(book.Craft(bag,2,false,true)==2&&bag.Crafting2.All(s=>s is null)&&bag.Cursor?.Count==2,"repeat crafting consumes one of each shaped input per result");
        var before=bag.Capture();
        Check(book.Craft(bag,2)==0&&bag.Matches(before),"empty or stale output cannot create items");
        bag=new(r);bag.TryAdd(new(new("blockcircuit:coal"),9));bag.Click(new(ItemArea.Storage,0));
        bag.Drag(Enumerable.Range(0,9).Select(i=>new ItemSlot(ItemArea.Craft3,i)),true);
        Check(book.Find(bag,3)?.Result.Id==new ContentId("blockcircuit:coal_block")&&book.Find(bag,2) is null,"3x3 compression requires all nine cells and cannot match a 2x2 grid");
        bag.TryAdd(new(log,1));bag.Click(new(ItemArea.Storage,0));
        before=bag.Capture();
        Check(book.Craft(bag,3)==0&&bag.Matches(before),"incompatible carried output rejects crafting without consuming ingredients");
        const string asymmetric="""[{"id":"test:shape","type":"shaped","pattern":["AB"],"key":{"A":"blockcircuit:stone","B":"blockcircuit:dirt"},"result":{"item":"blockcircuit:sand","count":1}}]""";
        var custom=RecipeBook.LoadJson(r,asymmetric);
        bag=new(r);bag.TryAdd(new(new("blockcircuit:dirt"),1));bag.TryAdd(new(new("blockcircuit:stone"),1));
        bag.Click(new(ItemArea.Storage,0));bag.Click(new(ItemArea.Craft3,7));
        bag.Click(new(ItemArea.Storage,1));bag.Click(new(ItemArea.Craft3,8));
        Check(custom.Find(bag,3) is not null,"shaped recipes match shifted and horizontally mirrored patterns");
        Check(InventoryTests.Rejected(()=>RecipeBook.LoadJson(r,asymmetric.Replace("sand","unknown"))),"unknown recipe items are rejected");
        Check(InventoryTests.Rejected(()=>RecipeBook.LoadJson(r,asymmetric.Replace("\"count\":1","\"count\":0"))),"zero recipe results are rejected");
        var full=new PlayerInventory(r);full.TryAdd(new(new("blockcircuit:stone"),36*64));
        var state=full.Capture();var grid=state.Craft2!.ToArray();grid[0]=new(log.ToString(),1);
        full=PlayerInventory.Restore(r,state with{Craft2=grid});before=full.Capture();
        Check(book.Craft(full,2,true)==0&&full.Matches(before),"full destination rejects crafting atomically");
        const string mixed="""[{"id":"test:mixed","type":"shapeless","ingredients":["blockcircuit:stone","blockcircuit:dirt","blockcircuit:stone"],"result":{"item":"blockcircuit:gravel","count":2}}]""";
        var mixedBook=RecipeBook.LoadJson(r,mixed);
        bag=new(r);bag.TryAdd(new(new("blockcircuit:stone"),2));bag.TryAdd(new(new("blockcircuit:dirt"),1));
        bag.Click(new(ItemArea.Storage,0));bag.Drag([new(ItemArea.Craft2,0),new(ItemArea.Craft2,2)],true);
        bag.Click(new(ItemArea.Storage,1));bag.Click(new(ItemArea.Craft2,1));
        Check(mixedBook.Craft(bag,2,true)==2&&bag.Crafting2.All(s=>s is null),"shapeless recipes consume an exact multiset including repeated ingredients");
        bag=new(r);bag.TryAdd(new(log,1));bag.TryAdd(new(planks,1));
        bag.Click(new(ItemArea.Storage,0));bag.Click(new(ItemArea.Craft2,0));
        bag.Click(new(ItemArea.Storage,1));bag.Click(new(ItemArea.Craft2,3));
        Check(book.Find(bag,2) is null,"unrelated extra grid inputs prevent a partial recipe match");
        Console.WriteLine($"Crafting tests passed: {checks}");
    }
}
