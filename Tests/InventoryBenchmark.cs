using System.Diagnostics;
using System.Text.Json;
namespace BlockCircuit;

public static class InventoryBenchmark
{
    public static void Run()
    {
        var world=new World(false);System.Array.Clear(world.Inventory);world.EnableSlotInventory();
        world.Items!.TryAdd(new(new("blockcircuit:stone"),64));
        var initial=world.Items.Capture();var cell=new Cell(4,2,4);
        var edits=Measure(2000,()=>{
            if(!world.PlaceFromSlot(cell,0)||!world.Undo())throw new InvalidOperationException("Inventory edit benchmark failed.");
        });
        if(!world.Items.Matches(initial))throw new InvalidOperationException("Edit benchmark changed item layout.");
        var book=RecipeBook.LoadDefault(world.Registries.Items);var bag=new PlayerInventory(world.Registries.Items);
        bag.TryAdd(new(new("blockcircuit:coal"),9));bag.Click(new(ItemArea.Storage,0));bag.Drag(Enumerable.Range(0,9).Select(i=>new ItemSlot(ItemArea.Craft3,i)),true);
        var crafts=Measure(500,()=>{
            if(book.Craft(bag,3,true)!=1)throw new InvalidOperationException("Compression failed.");
            bag.Click(new(ItemArea.Storage,0));bag.Click(new(ItemArea.Craft2,0));
            if(book.Craft(bag,2,true)!=9)throw new InvalidOperationException("Unpacking failed.");
            bag.Click(new(ItemArea.Storage,0));bag.Drag(Enumerable.Range(0,9).Select(i=>new ItemSlot(ItemArea.Craft3,i)),true);
        });
        if(bag.Totals().Count!=1||bag.Totals()["blockcircuit:coal"]!=9)throw new InvalidOperationException("Craft cycle duplicated items.");
        File.WriteAllText("inventory-benchmark-results.json",JsonSerializer.Serialize(new{TimestampUtc=DateTime.UtcNow,Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Scope="Headless owner-thread inventory operations after 50 warmup cycles per scenario. Includes snapshot validation and history; no GPU, storage I/O or frame-time claim.",
            PlacementAndUndo=edits,CompressionAndUnpacking=crafts,ExactSlotLayoutPreserved=true,CraftCountsPreserved=true},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: inventory benchmark: 2000 place/undo cycles and 500 reversible crafting cycles.");
    }
    static object Measure(int count,Action action){
        for(int i=0;i<50;i++)action();
        var times=new double[count];long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<count;i++){long start=Stopwatch.GetTimestamp();action();times[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;Array.Sort(times);
        return new{Samples=count,P50Milliseconds=times[count/2],P95Milliseconds=times[(int)(count*.95)],P99Milliseconds=times[(int)(count*.99)],MaximumMilliseconds=times[^1],TotalMilliseconds=times.Sum(),AllocatedBytes=allocated};
    }
}
