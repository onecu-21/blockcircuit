using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlockCircuit;

public static class WorldLibraryTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string description){if(!value)throw new InvalidOperationException(description);Console.WriteLine("PASS: "+description);checks++;}
        string directory=Path.Combine(Path.GetTempPath(),"blockcircuit-world-library-"+Guid.NewGuid().ToString("N"));
        try
        {
            string empty=Path.Combine(directory,"empty");
            var library=new WorldLibrary(empty);
            Check(library.List().Count==0&&!Directory.Exists(empty),"opening an empty world library does not create a world or write its data directory");

            Directory.CreateDirectory(directory);
            var first=library.Create("My World",701,false);
            var second=library.Create("My World",701,false);
            Check(first.Entry.Id!=second.Entry.Id&&library.GetSavePath(first.Entry)!=library.GetSavePath(second.Entry),
                "duplicate world names and seeds still receive independent saved-world folders");
            Check(first.World.Seed==701&&first.World.IsStreaming&&first.World.TerrainVersion==2&&first.World.Items is not null,
                "a new natural world has its own seed, streaming generator and real slot inventory");
            Check(library.List().Count==2&&library.List().All(e=>e.CanPlay&&e.Name=="My World"),
                "created named worlds are listed without silently entering either world");
            string firstPath=library.GetSavePath(first.Entry);
            Check(Path.GetFullPath(firstPath).StartsWith(Path.GetFullPath(Path.Combine(empty,"worlds"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(firstPath),
                "a world name is not used as its directory path");

            var loaded=library.Open(first.Entry);
            Check(loaded.World.Seed==701&&loaded.World.Items is not null&&loaded.Position==first.World.Spawn(),
                "opening a selected world restores its saved seed, player inventory and position");
            var marker=new Cell(20,10,20);loaded.World.Set(marker,Block.DiamondBlock);
            loaded.World.Save(firstPath,loaded.Position,.4f,-.2f);
            var independent=library.Open(second.Entry);
            Check(independent.World.Get(marker)==Block.Air&&independent.World.Seed==701,
                "editing one world cannot change another world with the same name and seed");
            var reopened=library.Open(first.Entry);
            MakeResident(reopened.World,firstPath,marker);
            Check(reopened.World.Get(marker)==Block.DiamondBlock&&reopened.Yaw==.4f&&reopened.Pitch==-.2f,
                "re-entering a selected world preserves its blocks and camera independently");

            var classic=library.Create("Old Hills",-817,true);
            Check(classic.World.Seed==-817&&classic.World.TerrainVersion==0&&classic.World.Chunks.Chunks.Count>0&&classic.Entry.Classic,
                "classic generation is a real alternate terrain choice and retains the chosen seed");
            Check(library.Open(classic.Entry).World.Seed==-817,"classic worlds round trip through their own save");

            byte[] primary=File.ReadAllBytes(firstPath);
            var renamed=library.Rename(first.Entry.Id,"Renamed World");
            Check(renamed.Name=="Renamed World"&&renamed.Id==first.Entry.Id&&primary.SequenceEqual(File.ReadAllBytes(firstPath)),
                "renaming changes catalog metadata without rewriting terrain or inventory");
            Check(library.List().Single(e=>e.Id==second.Entry.Id).Name=="My World",
                "renaming a world does not rename another world's display label");
            var played=library.MarkPlayed(first.Entry.Id);
            Check(played.LastPlayedUtc>=played.CreatedUtc&&library.List().Single(e=>e.Id==first.Entry.Id).LastPlayedUtc==played.LastPlayedUtc,
                "last-played metadata is persisted for returning to the world list");

            var before=TreeHashes(empty);
            Check(Rejected(()=>library.Create(" ",1))&&Rejected(()=>library.Create(new string('n',65),1))&&
                Rejected(()=>library.Rename(first.Entry.Id,"bad\nname"))&&Same(before,TreeHashes(empty)),
                "invalid creation and rename inputs leave all existing files unchanged");

            bool acceptedPathName=false;
            try
            {
                var display=library.Create("../../display-only",8);
                string safe=Path.GetFullPath(library.GetSavePath(display.Entry));
                acceptedPathName=safe.StartsWith(Path.GetFullPath(Path.Combine(empty,"worlds"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
            }
            catch(Exception e)when(e is ArgumentException or InvalidDataException){acceptedPathName=true;}
            Check(acceptedPathName&&!Directory.Exists(Path.Combine(directory,"display-only")),
                "path-like display names are rejected or remain inside GUID-owned world folders");

            string corruptId=Guid.NewGuid().ToString("N");
            string corrupt=Path.Combine(empty,"worlds",corruptId);
            Directory.CreateDirectory(corrupt);File.WriteAllText(Path.Combine(corrupt,"world-info.json"),"{broken");
            var entries=library.List();
            Check(entries.Any(e=>e.Id.ToString("N")==corruptId&&!e.CanPlay&&e.Error is not null)&&entries.Any(e=>e.Id==first.Entry.Id&&e.CanPlay),
                "one corrupt world description is visible as an error without hiding healthy worlds");
            var corruptEntry=entries.Single(e=>e.Id.ToString("N")==corruptId);
            Check(Rejected(()=>library.Open(corruptEntry))&&primary.SequenceEqual(File.ReadAllBytes(firstPath)),
                "an unreadable selected world cannot alter another saved world");

            string settings=Path.Combine(empty,"settings.json");File.WriteAllText(settings,"settings sentinel");
            var remove=library.List().Single(e=>e.Id==second.Entry.Id);
            using(var writer=new FileStream(library.GetSavePath(remove)+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Check(Rejected(()=>library.Trash(remove.Id))&&library.List().Any(e=>e.Id==remove.Id&&e.CanPlay),
                    "removing a world with an active save lock fails without losing its catalog entry or save");
            string archive=library.Trash(remove.Id);
            Check(!library.List().Any(e=>e.Id==remove.Id)&&Directory.Exists(archive)&&
                Path.GetFullPath(archive).StartsWith(Path.GetFullPath(Path.Combine(empty,"trash"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
                "confirmed removal moves a whole world into recoverable library trash");
            string archived=Path.Combine(archive,"world.json");
            var archivedWorld=new World(false);archivedWorld.Load(archived);
            Check(archivedWorld.Seed==701&&File.ReadAllText(settings)=="settings sentinel"&&File.Exists(firstPath),
                "a trashed world remains loadable while other worlds and application settings remain intact");

            var beforeBackup=TreeHashes(empty).Where(p=>!p.Key.StartsWith("backups"+Path.DirectorySeparatorChar,StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
            string backup=library.Backup(first.Entry.Id);
            var restoredBackup=new World(false);restoredBackup.Load(backup);
            var afterBackup=TreeHashes(empty).Where(p=>!p.Key.StartsWith("backups"+Path.DirectorySeparatorChar,StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
            Check(restoredBackup.Get(marker)==Block.DiamondBlock&&restoredBackup.Items is not null&&restoredBackup.Seed==701,
                "an explicit world backup is a standalone file with streamed edits and slot state");
            Check(Same(beforeBackup,afterBackup),"making a backup does not rewrite the source world or its last-played metadata");
            TestLegacy(directory,Check);
            TestFailedCreate(directory,Check);
        }
        finally
        {
            string full=Path.GetFullPath(directory),temporary=Path.GetFullPath(Path.GetTempPath());
            if(!full.StartsWith(temporary,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||
               !Path.GetFileName(full).StartsWith("blockcircuit-world-library-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected world library cleanup path.");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
        Console.WriteLine($"World library tests passed: {checks}");
    }
    static void MakeResident(World world,string savePath,Cell cell)
    {
        if(world.IsCellAvailable(cell))return;
        using var streamer=new WorldStreamer(world,savePath,new StreamingOptions{MaxResidentColumns=4,MaxLoadJobs=2});
        var timeout=System.Diagnostics.Stopwatch.StartNew();
        while(!world.IsCellAvailable(cell)&&timeout.Elapsed<TimeSpan.FromSeconds(10))
        {
            streamer.Update(cell.Center,0);Thread.Sleep(1);
            if(streamer.LastError is not null)throw new InvalidOperationException("Could not load selected-world terrain.",streamer.LastError);
        }
        if(!world.IsCellAvailable(cell))throw new TimeoutException("Selected-world terrain did not become available.");
    }
    static void TestLegacy(string directory,Action<bool,string> check)
    {
        string root=Path.Combine(directory,"legacy");Directory.CreateDirectory(root);
        string path=Path.Combine(root,"world.json");
        var original=new World(false);original.Set(new(4,2,4),Block.RedWool);original.EnableSlotInventory();
        original.Items!.Click(new(ItemArea.Storage,0));
        original.Save(path,original.Spawn(),.15f,-.25f);
        original.Set(new(5,2,4),Block.BlueWool);original.Save(path,original.Spawn(),.15f,-.25f);
        File.WriteAllText(Path.Combine(root,"settings.json"),"legacy settings");
        var before=TreeHashes(root);
        var library=new WorldLibrary(root);
        var entries=library.List();var legacy=entries.Single(e=>e.Legacy);
        check(legacy.CanPlay&&Same(before,TreeHashes(root)),"existing world.json is discovered without rewriting it, its chunk files, recovery save or settings");
        var loaded=library.Open(legacy);
        check(loaded.World.Get(new(4,2,4))==Block.RedWool&&loaded.World.Get(new(5,2,4))==Block.BlueWool&&
            loaded.World.Items!.Cursor is not null&&loaded.Yaw==.15f,
            "opening the legacy entry restores existing blocks, slot cursor and camera rather than generating new terrain");
        var fresh=library.Create("A Separate World",12);
        check(File.ReadAllBytes(path).SequenceEqual(before["world.json"])&&library.GetSavePath(legacy)==path&&library.List().Count==2,
            "creating a new catalog world leaves the original legacy save in its original location");
        string archive=library.Trash(legacy.Id);
        check(!library.List().Any(e=>e.Legacy)&&File.Exists(Path.Combine(root,"settings.json"))&&File.Exists(library.GetSavePath(fresh.Entry))&&Directory.Exists(archive),
            "removing a legacy entry archives only that world's artifacts and preserves settings and newer catalog worlds");

        string recoveryRoot=Path.Combine(directory,"recovery");Directory.CreateDirectory(recoveryRoot);
        var source=new World(false);source.Set(new(4,2,4),Block.Glass);
        source.Save(Path.Combine(recoveryRoot,"world.json"),source.Spawn(),0,0);
        source.Save(Path.Combine(recoveryRoot,"world.json"),source.Spawn(),0,0);
        File.Delete(Path.Combine(recoveryRoot,"world.json"));
        var recoveryLibrary=new WorldLibrary(recoveryRoot);var recovery=recoveryLibrary.List().Single(e=>e.Legacy);
        check(recoveryLibrary.Open(recovery).World.Get(new(4,2,4))==Block.Glass,
            "a legacy recovery save remains discoverable when the current world.json is missing");
    }
    static void TestFailedCreate(string directory,Action<bool,string> check)
    {
        string root=Path.Combine(directory,"blocked");Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"worlds"),"blocking file");
        File.WriteAllText(Path.Combine(root,"settings.json"),"unchanged");
        var library=new WorldLibrary(root);
        var before=TreeHashes(root);
        check(Rejected(()=>library.Create("Cannot Save",19))&&Same(before,TreeHashes(root)),
            "a filesystem failure while creating a world does not overwrite existing application files");
    }
    static Dictionary<string,byte[]> TreeHashes(string directory)=>!Directory.Exists(directory)?[]:
        Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(directory,p),File.ReadAllBytes,StringComparer.Ordinal);
    static bool Same(Dictionary<string,byte[]> a,Dictionary<string,byte[]> b)=>a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var data)&&p.Value.SequenceEqual(data));
    static bool Rejected(Action action)
    {
        try{action();return false;}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException){return true;}
    }
}
