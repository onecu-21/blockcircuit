using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace BlockCircuit;

public static class InventoryPersistenceTests
{
    public static void Run(){
        int checks=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);Console.WriteLine("PASS: "+name);checks++;}
        string directory=Path.Combine(Path.GetTempPath(),"blockcircuit-items-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"world.json"),snapshot=Path.Combine(directory,"portable.json");
        var options=new JsonSerializerOptions{IncludeFields=true};
        try{
            var world=new World(false);
            var legacy=world.Inventory.ToArray();world.EnableSlotInventory();
            Check(world.Items is not null&&legacy.SequenceEqual(world.Inventory)&&world.Items.Reserve.Count>0,"migration preserves every legacy count including stock beyond 36 slots");
            Check(world.Items!.Slots.Skip(9).All(s=>s is null),"migrated starting stock leaves the 27 backpack slots available for mining");
            var bag=world.Items!;
            var before=bag.Capture();
            Check(world.PlaceFromSlot(new(5,2,5),0)&&bag.Slots[0]?.Count==63,"placement consumes only the selected real hotbar stack");
            Check(world.Undo()&&bag.Matches(before)&&world.Get(new(5,2,5))==Block.Air,"undo restores exact slot layout and counts");
            Check(world.Redo()&&bag.Slots[0]?.Count==63,"redo reapplies one slot debit without duplication");
            bag.Click(new(ItemArea.Storage,0));
            Check(world.UndoCount==0&&!world.Undo(),"inventory interaction invalidates incompatible edit history");
            bag.Click(new(ItemArea.Craft2,3),true);
            Check(world.DropItem(null,1,new(7.5f,2,7.5f))&&world.DroppedItems.Count==1,"dropping debits the cursor and creates one persistent world pickup");
            var expected=world.CaptureInventory();
            world.Save(path,world.Spawn(),.2f,-.3f);
            var loaded=new World(false);loaded.Load(path);
            Check(JsonSerializer.Serialize(expected)==JsonSerializer.Serialize(loaded.CaptureInventory()),"chunk save restores slots, reserve, cursor, crafting grids and pickup IDs");
            loaded.ExportSnapshot(snapshot,loaded.Spawn(),0,0);
            var portable=new World(false);portable.Load(snapshot);
            Check(JsonSerializer.Serialize(loaded.CaptureInventory())==JsonSerializer.Serialize(portable.CaptureInventory()),"portable v3 backup retains the entire item state independently");
            Check(loaded.CollectDrops(new(7.5f,2,7.5f))==0,"pickup delay prevents immediate recollection");
            for(int i=0;i<20;i++)loaded.Tick();
            Check(loaded.CollectDrops(new(7.5f,2,7.5f))==1&&loaded.DroppedItems.Count==0,"20 TPS pickup transfers once after its delay");
            var total=loaded.Items!.Totals();
            for(int i=0;i<30;i++){
                loaded.Items!.Click(new(ItemArea.Storage,i%36),i%2==0);
                loaded.Save(path,loaded.Spawn(),0,0);
                loaded=new World(false);loaded.Load(path);
            }
            Check(InventoryTests.Equal(total,loaded.Items!.Totals()),"30 edit/save/reload cycles preserve every item total");
            loaded.Save(path,loaded.Spawn(),0,0);
            var json=JsonNode.Parse(File.ReadAllText(path))!;
            json["ItemInventory"]!["Player"]!["Slots"]![0]=new JsonObject{{"Id","blockcircuit:stone"},{"Count",65}};
            File.WriteAllText(path,json.ToJsonString());File.Delete(path+".bak");
            var live=loaded.CaptureInventory();var cells=loaded.Blocks;
            Check(InventoryTests.Rejected(()=>loaded.Load(path))&&JsonSerializer.Serialize(live)==JsonSerializer.Serialize(loaded.CaptureInventory())&&cells.SequenceEqual(loaded.Blocks),"invalid slots reject loading before mutating live world or inventory");
            File.Delete(path);
            loaded.Save(path,loaded.Spawn(),0,0);
            byte[] committed=File.ReadAllBytes(path);
            bool failed=false;try{ChunkSaveStore.Save(loaded,path,loaded.Spawn(),0,0,phase=>{if(phase==SaveCheckpoint.BeforeManifestCommit)throw new IOException("injected");});}catch(IOException){failed=true;}
            Check(failed&&committed.SequenceEqual(File.ReadAllBytes(path)),"injected save failure preserves the last committed inventory manifest");
            var empty=new World(false);System.Array.Clear(empty.Inventory);empty.EnableSlotInventory();
            empty.Items!.TryAdd(new(new("blockcircuit:stone"),36*64));empty.Set(new(4,2,4),Block.Dirt);
            Check(!empty.Mine(new(4,2,4))&&empty.Get(new(4,2,4))==Block.Dirt,"a full inventory cannot destroy an uncollectable mined block");
            var customRegistries=GameRegistries.Create(r=>r.Items.Register(new("example:token"),new("Token",16)));
            var modded=new World(false,registries:customRegistries);System.Array.Clear(modded.Inventory);modded.EnableSlotInventory();
            modded.Items!.TryAdd(new(new("example:token"),12));modded.Save(path,modded.Spawn(),0,0);
            var missing=new World(false);missing.Load(path);missing.Save(path,missing.Spawn(),0,0);
            var returned=new World(false,registries:customRegistries);returned.Load(path);
            Check(returned.Items!.Totals().GetValueOrDefault("example:token")==12&&returned.Items.Slots[0]?.Count==12,"missing mod items survive resave and reinstall with their slot layout");
            var old=new World(false);
            var oldData=new World.SaveData(2,old.Blocks,[],old.Inventory.Take(180).ToArray(),old.Spawn(),0,0);
            File.WriteAllText(snapshot,JsonSerializer.Serialize(oldData,options));
            old.Load(snapshot);old.EnableSlotInventory();
            Check(old.Inventory.Length==181&&old.Inventory[(int)Block.Stone]==128,"original 180-entry v2 saves migrate after the crafting table was appended");
            var current=new World(false);current.EnableSlotInventory();current.Save(path,current.Spawn(),0,0);
            var state=current.CaptureInventory();
            var created=WorldSession.Create(current,current.Spawn(),0,0,188,false,path);
            var backup=new World(false);backup.Load(created.BackupPath);
            Check(created.World.Items is not null&&JsonSerializer.Serialize(state)==JsonSerializer.Serialize(backup.CaptureInventory()),"new-world creation preserves the previous inventory in its standalone backup");
            var pickup=new World(false);System.Array.Clear(pickup.Inventory);pickup.EnableSlotInventory();
            pickup.Items!.TryAdd(new(new("blockcircuit:dirt"),1));pickup.DropItem(new(ItemArea.Storage,0),1,pickup.Spawn());
            pickup.Items.TryAdd(new(new("blockcircuit:stone"),36*64));
            for(int i=0;i<20;i++)pickup.Tick();
            Check(pickup.CollectDrops(pickup.Spawn())==0&&pickup.DroppedItems.Count==1,"a full bag leaves a dropped pickup intact for a later attempt");
            pickup.Items.Take(new(ItemArea.Storage,0),64);
            Check(pickup.CollectDrops(pickup.Spawn())==1&&pickup.CollectDrops(pickup.Spawn())==0,"a later pickup succeeds exactly once after space becomes available");
            var mining=new World(false);System.Array.Clear(mining.Inventory);mining.EnableSlotInventory();mining.Set(new(4,2,4),Block.Stone);
            var miningBefore=mining.Items!.Capture();
            Check(mining.Mine(new(4,2,4))&&mining.Undo()&&mining.Items.Matches(miningBefore)&&mining.Get(new(4,2,4))==Block.Stone,"mining undo restores exact empty slots and the original world cell");
            TestMigrationBackup(directory,Check);
            string recoveryPath=Path.Combine(directory,"recovery-items.json");
            var broken=new World(false);broken.Set(new(2,2,2),Block.Stone);broken.Save(recoveryPath,broken.Spawn(),0,0);
            broken.Set(new(2,2,2),Block.Dirt);broken.Save(recoveryPath,broken.Spawn(),0,0);
            using(var doc=JsonDocument.Parse(File.ReadAllText(recoveryPath)))
                File.Delete(Path.Combine(recoveryPath+".chunks",doc.RootElement.GetProperty("Chunks")[0].GetProperty("File").GetString()!));
            var recovery=new World(false);recovery.Load(recoveryPath);recovery.EnableSlotInventory();recovery.Save(recoveryPath,recovery.Spawn(),0,0);
            var repaired=new World(false);repaired.Load(recoveryPath);
            Check(repaired.Items is not null&&repaired.Get(new(2,2,2))==Block.Stone,"migration after backup recovery repairs a primary with missing chunk files");
            TestAsync(directory,Check);
        }finally{
            string full=Path.GetFullPath(directory);
            if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("blockcircuit-items-"))throw new InvalidOperationException("Unexpected temporary path.");
            Directory.Delete(full,true);
        }
        Console.WriteLine($"Inventory persistence tests passed: {checks}");
    }
    static void TestMigrationBackup(string directory,Action<bool,string> check){
        string path=Path.Combine(directory,"migrate-v4.json");var old=new World(false);old.Set(new(3,2,3),Block.Stone);old.Save(path,old.Spawn(),0,0);
        byte[] original=File.ReadAllBytes(path);old.EnableSlotInventory();old.Save(path,old.Spawn(),0,0);
        check(Directory.GetFiles(Path.Combine(directory,"backups"),"migrate-v4-legacy-v4-*.json").Any(p=>File.ReadAllBytes(p).SequenceEqual(original)),"the first slot save preserves an untouched original v4 migration backup");
        old.Set(new(3,2,3),Block.Dirt);old.Save(path,old.Spawn(),0,0);old.Save(path,old.Spawn(),0,0);
        string backup=Directory.GetFiles(Path.Combine(directory,"backups"),"migrate-v4-legacy-v4-*.json").Single();
        var recovered=new World(false);recovered.Load(backup);
        check(recovered.Get(new(3,2,3))==Block.Stone,"v4 migration backup owns its chunk files after later saves prune the original versions");
    }
    static void TestAsync(string directory,Action<bool,string> check){
        string path=Path.Combine(directory,"async.json");var world=World.CreateStreaming();world.EnableSlotInventory();
        var expected=world.CaptureInventory();
        using(var session=new StreamingSaveSession(world,path)){
            session.Request(world.Spawn());session.Pump(2);
            world.Items!.Click(new(ItemArea.Storage,0));
            session.Flush(world.Spawn(),0,0);
        }
        var restored=World.CreateStreaming();restored.Load(path);
        check(restored.Items!.Matches(world.Items!.Capture()),"background save and final flush capture the newest owner-thread item state without shared live arrays");
        var detached=new World(false);detached.CopyMetadataFrom(world);
        var before=detached.CaptureInventory();world.Items.Click(new(ItemArea.Storage,2));
        check(JsonSerializer.Serialize(before)==JsonSerializer.Serialize(detached.CaptureInventory()),"detached streaming metadata owns independent inventory, crafting and cursor arrays");
    }
}
