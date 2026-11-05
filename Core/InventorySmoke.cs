using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    public void RunInventorySmoke()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow|ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(1280,800,"Block Circuit | inventory validation");Raylib.SetWindowMinSize(960,640);Raylib.SetTargetFPS(60);Raylib.SetExitKey(KeyboardKey.Null);
        renderer=new BlockRenderer();
        world=new World(false);System.Array.Clear(world.Inventory);world.EnableSlotInventory();SyncHotbar();
        feet=world.Spawn();previousFeet=feet;
        for(int x=15;x<=25;x++)for(int z=15;z<=27;z++)world.Set(new(x,0,z),Block.Stone);
        var bench=new Cell(20,1,20);world.Set(bench,Block.CraftingTable);
        var bag=world.Items!;var oak=LegacyBlockIds.GetPersistentId((int)Block.OakPlanks);
        bag.TryAdd(new(oak,8));bag.Click(new(ItemArea.Storage,0));bag.Drag(Enumerable.Range(0,4).Select(i=>new ItemSlot(ItemArea.Craft2,i)),false);
        inventoryOpen=true;craftingSize=2;libraryView=false;
        int crafted=0;bool saved=false,dropped=false;double maximumFrame=0;
        string path=Path.Combine(Path.GetTempPath(),"blockcircuit-inventory-smoke-"+Guid.NewGuid().ToString("N")+".json");
        try{
            for(int frame=0;frame<100;frame++){
                var timer=System.Diagnostics.Stopwatch.StartNew();
                if(frame==20){
                    if(recipeBook.Find(bag,2)?.Result.Id!=new ContentId("blockcircuit:crafting_table"))throw new InvalidOperationException("Personal recipe preview failed.");
                    Raylib.TakeScreenshot("inventory-preview.png");
                    crafted=recipeBook.Craft(bag,2,true,true);
                    if(crafted!=2)throw new InvalidOperationException("Table crafting did not conserve inputs.");
                    bag.TryAdd(new(new("blockcircuit:coal"),9));
                    bag.Click(new(ItemArea.Storage,1));bag.Drag(Enumerable.Range(0,9).Select(i=>new ItemSlot(ItemArea.Craft3,i)),true);
                    OpenCrafting(bench);
                }
                if(frame==45){
                    if(recipeBook.Find(bag,3)?.Result.Id!=new ContentId("blockcircuit:coal_block"))throw new InvalidOperationException("Workbench preview failed.");
                    Raylib.TakeScreenshot("crafting-preview.png");
                    if(recipeBook.Craft(bag,3,true)!=1)throw new InvalidOperationException("Workbench craft failed.");
                    if(!world.DropItem(new(ItemArea.Storage,0),1,feet+new Vector3(0,.5f,-1)))throw new InvalidOperationException("Q-style drop failed.");
                    dropped=world.DroppedItems.Count==1;
                    world.Save(path,feet,0,0);
                    var restored=new World(false);restored.Load(path);
                    saved=restored.Items!.Matches(bag.Capture())&&restored.DroppedItems.Count==1;
                    if(!saved)throw new InvalidOperationException("UI item state did not round trip.");
                }
                if(frame==65){libraryView=true;}
                if(frame==80){Raylib.TakeScreenshot("inventory-library-preview.png");libraryView=false;Raylib.SetWindowSize(960,640);}
                Render(CurrentEye());
                if(frame==99)Raylib.TakeScreenshot("inventory-small-preview.png");
                maximumFrame=Math.Max(maximumFrame,timer.Elapsed.TotalMilliseconds);
            }
        }finally{renderer.Dispose();Raylib.CloseWindow();TestSaveFiles.Cleanup(path);}
        File.WriteAllText("inventory-smoke-results.json",JsonSerializer.Serialize(new{Passed=true,Frames=100,CraftedTables=crafted,SavedAndRestored=saved,DroppedPickupRendered=dropped,
            MaximumFrameMilliseconds=maximumFrame,RendererDisposed=true},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: inventory smoke: 2x2 / 3x3 / library / 960x640, item drop and save round trip.");
    }
}
