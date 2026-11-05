namespace BlockCircuit;
public static class InventoryGestureTests
{
    public static void Run(){
        int checks=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);Console.WriteLine("PASS: "+name);checks++;}
        var bag=new PlayerInventory(GameRegistries.Default.Items);var gesture=new InventoryGesture();
        bag.TryAdd(new(new("blockcircuit:stone"),16));
        var source=new ItemSlot(ItemArea.Storage,0);var a=new ItemSlot(ItemArea.Storage,1);var b=new ItemSlot(ItemArea.Storage,2);
        gesture.Begin(bag,source,false,false);gesture.Hover(bag,source);gesture.Release(bag);
        Check(bag.Cursor?.Count==16&&bag.Get(source) is null,"press and release on a source picks up without immediately putting it back");
        gesture.Begin(bag,a,false,false);gesture.Hover(bag,b);gesture.Release(bag);
        Check(bag.Get(a)?.Count==8&&bag.Get(b)?.Count==8&&bag.Cursor is null,"left drag with a carried stack spreads across visited slots");
        gesture.Begin(bag,a,true,false);gesture.Release(bag);
        Check(bag.Cursor?.Count==4&&bag.Get(a)?.Count==4,"right click source splits half using the same UI gesture controller");
        gesture.Begin(bag,source,true,false);gesture.Hover(bag,b);gesture.Release(bag);
        Check(bag.Get(source)?.Count==1&&bag.Get(b)?.Count==9&&bag.Cursor?.Count==2,"right drag places one at each distinct visited destination");
        var before=bag.Capture();gesture.Begin(bag,source,false,false);gesture.Cancel();
        Check(bag.Matches(before),"canceling a held-stack gesture before release does not discard the cursor");
        gesture.Begin(bag,a,false,true);
        Check(!gesture.Active&&bag.Get(a) is null&&bag.Slots[9]?.Count==4,"shift click immediately transfers without a pending drag");
        var replacement=new PlayerInventory(GameRegistries.Default.Items);var empty=replacement.Capture();
        gesture.Begin(bag,source,false,false);gesture.Hover(replacement,b);gesture.Release(replacement);
        Check(replacement.Matches(empty),"a stale drag cannot transfer items into a replaced world inventory");
        Console.WriteLine($"Inventory gesture tests passed: {checks}");
    }
}
