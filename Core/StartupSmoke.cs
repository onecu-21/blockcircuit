using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    /// <summary>Exercises the normal first-launch handlers with an isolated library and a real GL context.</summary>
    public static void RunStartupSmoke()
    {
        string directory=Path.Combine(Path.GetTempPath(),"blockcircuit-startup-smoke-"+Guid.NewGuid().ToString("N"));
        var game=new Game(dataRoot:directory);
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow|ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(1280,800,"Block Circuit | startup validation");
        Raylib.SetWindowMinSize(960,640);Raylib.SetTargetFPS(60);Raylib.SetExitKey(KeyboardKey.Null);Raylib.EnableCursor();
        game.renderer=new BlockRenderer{DrawDistance=32};
        game.settings.DrawDistance=32;
        int checks=0,frames=0,loadingFrames=0;
        string? failure=null;
        void Check(bool result,string description)
        {
            if(!result)throw new InvalidOperationException(description);
            checks++;Console.WriteLine("PASS: "+description);
        }
        void Frame()
        {
            game.PumpMultiplayer();
            game.UpdateFrontend();
            if(game.HasPlayableWorld){game.EnsureStreaming();game.Render(game.CurrentEye());}else game.RenderFrontend();
            frames++;if(game.startup.Screen==StartupScreen.Loading)loadingFrames++;
        }
        void Capture(string name){Frame();Raylib.TakeScreenshot("startup-"+name+"-preview.png");}
        void Enter()
        {
            var timer=Stopwatch.StartNew();
            while(!game.HasPlayableWorld&&timer.Elapsed<TimeSpan.FromSeconds(25)){
                Frame();
                if(!game.HasPlayableWorld&&game.startup.Screen!=StartupScreen.Loading)throw new InvalidOperationException("World launch failed: "+game.frontendStatus);
            }
            Check(game.HasPlayableWorld&&game.SpawnAreaReady(),"selected world enters only after spawn terrain and the renderer are prepared");
            Check(!Raylib.IsCursorOnScreen()||Raylib.IsCursorHidden(),"world entry captures the mouse");
        }
        try
        {
            Check(game.startup.Screen==StartupScreen.Title&&game.world.Chunks.Chunks.Count==0&&game.streamer is null&&!Directory.Exists(directory),
                "normal first launch shows the title without generating, loading or writing a world");
            Capture("title");
            Check(game.TrySave()&&!Directory.Exists(directory),"closing an untouched title cannot save a placeholder world");
            game.ShowSingleplayer();
            Check(game.startup.Screen==StartupScreen.CreateWorld,"empty singleplayer offers first-world creation");
            Capture("create-game");game.creationTab=1;Capture("create-world");
            game.BackFromFrontend();
            Check(game.startup.Screen==StartupScreen.Worlds&&!Directory.Exists(directory),"canceling creation returns to an empty list without files");
            Capture("empty-worlds");game.ShowCreateScreen();
            game.worldNameInput.Text="Meadow Lab";game.worldSeedInput.Text="2026";
            game.BeginCreatedWorld();
            Check(game.startup.Screen==StartupScreen.Loading&&game.worldLaunchTask is null,"creation queues I/O until a loading frame has been drawn");
            game.UpdateFrontend();
            Check(game.worldLaunchTask is null,"world generation cannot begin before the first loading frame");
            Capture("loading");Enter();
            Check(game.world.Solid(new(20,9,23))&&!game.world.Solid(new(20,10,23))&&!game.world.Solid(new(20,11,23)),"a newly created world has a solid spawn floor and clear body/head space");
            for(int warm=0;warm<24;warm++)Frame();Capture("playing");
            Check(game.activeEntry?.Name=="Meadow Lab"&&game.world.Seed==2026&&game.world.TerrainVersion==2,"creation applies the entered name, seed and natural terrain");
            Check(!game.startup.AcceptGameplayInput(true,false,false)&&!game.startup.AcceptGameplayInput(false,false,false)&&game.startup.AcceptGameplayInput(false,false,false),
                "the create button press is released before gameplay actions are accepted");
            var marker=new Cell(20,12,22);
            Check(game.world.IsCellAvailable(marker),"the edited spawn column is resident");
            game.world.Set(marker,Block.BlueWool);
            game.world.Items!.TryAdd(new(new("blockcircuit:diamond"),3));
            game.world.Items.Click(new(ItemArea.Storage,0));
            var inventory=game.world.Items.Capture();
            game.yaw=.55f;game.pitch=-.12f;var position=game.feet;
            var firstId=game.activeEntry!.Id;string firstPath=game.savePath;
            game.paused=true;Raylib.EnableCursor();Capture("pause");
            Check(game.SaveAndReturnToTitle()&&game.startup.Screen==StartupScreen.Title&&game.renderer.CachedChunkCount==0&&game.streamer is null,
                "save and quit returns to the title and releases the world's streaming and mesh cache");
            Check(File.Exists(firstPath)&&!File.Exists(Path.Combine(directory,"world.json")),"a named world saves only to its own GUID folder");
            game.ShowSingleplayer();Check(game.startup.Screen==StartupScreen.Worlds&&game.worldEntries.Length==1,"subsequent singleplayer opens the saved-world list");
            Capture("world-list");game.selectedWorldId=firstId;game.BeginSelectedWorld();Enter();
            Check(game.world.Get(marker)==Block.BlueWool&&game.world.Items!.Matches(inventory)&&game.feet==position&&game.yaw==.55f&&game.pitch==-.12f,
                "re-entering restores the selected world's edits, carried items and camera");
            Check(game.SaveAndReturnToTitle(),"reopened world saves successfully");
            byte[] firstSave=File.ReadAllBytes(firstPath);
            game.ShowSingleplayer();game.ShowCreateScreen();game.worldNameInput.Text="Ridge Build";game.worldSeedInput.Text="777";game.classicTerrain=true;
            game.BeginCreatedWorld();Enter();
            Check(game.activeEntry!.Id!=firstId&&game.world.Seed==777&&game.world.TerrainVersion==0&&firstSave.SequenceEqual(File.ReadAllBytes(firstPath)),
                "creating a second classic world uses independent storage and leaves the first save untouched");
            string secondPath=game.savePath;var activeWorld=game.world;
            // This is a real writer conflict, not a mocked save result.
            using(var locked=new FileStream(secondPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
                Check(!game.SaveAndReturnToTitle()&&game.HasPlayableWorld&&ReferenceEquals(game.world,activeWorld)&&game.frontendStatus.StartsWith("Save failed:"),
                    "a failed save retains the playable world and reports why quitting was blocked");
            }
            Check(game.SaveAndReturnToTitle(),"save and quit retries successfully after the writer lock is released");
            game.ShowSingleplayer();game.selectedWorldId=firstId;
            game.startup.ShowRename();game.renameInput.Text="Meadow Lab";Capture("edit-world");
            game.worldLibrary.Rename(firstId,"Meadow Home");game.RefreshWorlds();game.startup.Back();
            Check(game.SelectedWorld()?.Name=="Meadow Home"&&firstSave.SequenceEqual(File.ReadAllBytes(firstPath)),"renaming changes the label without rewriting saved terrain");
            game.startup.ShowDelete();Capture("delete-confirmation");game.BackFromFrontend();
            Check(game.worldLibrary.List().Count==2&&File.Exists(firstPath),"canceling the delete confirmation preserves both worlds");
            game.worldSearch.Text="Meadow";Check(game.FilteredWorlds().Length==1,"the saved-world search filters by display name");
            Raylib.SetWindowSize(960,640);Capture("small-world-list");game.BackFromFrontend();Capture("small-title");
            game.OpenSettings();Capture("options");game.CloseSettings();
            Check(game.FrontendVisible&&!Raylib.IsCursorHidden(),"closing title options keeps the frontend cursor available");

            var hostWorld=new World(false);hostWorld.EnableSlotInventory();
            for(int x=0;x<40;x++)for(int z=0;z<40;z++)hostWorld.Set(new(x,0,z),Block.Stone);
            using(var host=new MultiplayerHost(hostWorld,0,"","Host",IPAddress.Loopback)){
                game.OpenMultiplayer();game.profileLoaded=true;game.networkIdentity=Guid.NewGuid();game.networkName="MenuGuest";
                game.networkPort=host.Port.ToString();game.networkAddress="127.0.0.1";Capture("multiplayer");
                game.BeginJoining();
                var timer=Stopwatch.StartNew();
                while(!game.NetworkClientActive&&timer.Elapsed<TimeSpan.FromSeconds(10)){
                    host.Pump(new(hostWorld.Spawn(),0,0,false));Frame();
                    if(game.multiplayerClient?.Error is {} error)throw new InvalidOperationException(error);
                }
                Check(game.NetworkClientActive&&game.HasPlayableWorld&&game.world.IsNetworkReplica,"direct IP joining from the title enters a real TCP session");
                Check(!File.Exists(Path.Combine(directory,"world.json"))&&firstSave.SequenceEqual(File.ReadAllBytes(firstPath)),
                    "joining from the title does not save the placeholder or modify local worlds");
                Check(game.SaveAndReturnToTitle()&&game.startup.Screen==StartupScreen.Title&&!game.NetworkActive&&!game.NetworkConnecting,
                    "disconnecting a title-origin session returns to the title without saving its replica");
                // A failed authentication must leave the same title available for another attempt.
                using var protectedHost=new MultiplayerHost(hostWorld,0,"secret","Host",IPAddress.Loopback);
                game.OpenMultiplayer();game.networkPort=protectedHost.Port.ToString();game.networkPassword="wrong";game.BeginJoining();
                timer.Restart();
                while((game.NetworkConnecting||game.NetworkClientActive)&&timer.Elapsed<TimeSpan.FromSeconds(10)){
                    protectedHost.Pump(new(hostWorld.Spawn(),0,0,false));Frame();
                }
                Check(game.startup.Screen==StartupScreen.Title&&!game.NetworkConnecting&&!game.NetworkClientActive&&game.networkStatus.StartsWith("Disconnected:"),
                    "a rejected title connection reports its error and leaves local worlds available");
                game.CloseMultiplayer();
                game.OpenMultiplayer();game.networkPassword="secret";game.BeginJoining();
                Check(game.NetworkConnecting,"a title connection can be canceled while it is pending");
                game.CloseMultiplayer();
                Check(!game.NetworkConnecting&&!game.NetworkClientActive&&game.startup.Screen==StartupScreen.Title,"the multiplayer Back button cancels pending title connections");
                game.ShowSingleplayer();game.ShowCreateScreen();
                for(int settle=0;settle<5;settle++){protectedHost.Pump(new(hostWorld.Spawn(),0,0,false));Frame();}
                Check(game.startup.Screen==StartupScreen.CreateWorld&&!game.NetworkClientActive,"a late canceled connection cannot replace a new-world form or crash navigation");
                game.BackFromFrontend();game.BackFromFrontend();
                game.OpenMultiplayer();game.BeginJoining();timer.Restart();
                while(!game.NetworkClientActive&&timer.Elapsed<TimeSpan.FromSeconds(10)){protectedHost.Pump(new(hostWorld.Spawn(),0,0,false));Frame();}
                Check(game.NetworkClientActive,"a new title connection succeeds after cancellation");
                protectedHost.Dispose();timer.Restart();
                while(game.NetworkClientActive&&timer.Elapsed<TimeSpan.FromSeconds(10))Frame();
                Check(game.startup.Screen==StartupScreen.Title&&game.multiplayerOpen&&game.networkStatus.StartsWith("Disconnected:"),"host loss returns to the title with a visible connection error");
                Capture("connection-error");game.CloseMultiplayer();
            }
        }
        catch(Exception e){failure=e.ToString();}
        finally
        {
            game.LeaveMultiplayer();game.FinishFrontendJobs();game.StopStreaming(false);game.renderer.Dispose();Raylib.CloseWindow();
            string full=Path.GetFullPath(directory),temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(temporary,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||
               !Path.GetFileName(full).StartsWith("blockcircuit-startup-smoke-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected startup smoke cleanup path.");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
        bool resources=game.renderer.GpuBufferCount==0&&game.renderer.GpuAllocationCount==game.renderer.ReleasedMeshCount;
        File.WriteAllText("startup-smoke-results.json",JsonSerializer.Serialize(new{
            Passed=failure is null&&resources,Checks=checks,Frames=frames,LoadingFrames=loadingFrames,
            NativeWindow=true,IsolatedWorldLibrary=true,RealLoopbackTcp=true,AllGpuResourcesReleased=resources,
            GpuAllocations=game.renderer.GpuAllocationCount,GpuReleases=game.renderer.ReleasedMeshCount,Error=failure
        },new JsonSerializerOptions{WriteIndented=true}));
        if(failure is not null||!resources)throw new InvalidOperationException(failure??"Startup GPU resources leaked.");
        Console.WriteLine($"PASS: startup smoke: {checks} checks; title, create, select, save/quit, failure/retry, TCP and GPU teardown.");
    }
}
