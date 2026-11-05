using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    readonly StartupFlow startup;
    readonly WorldLibrary worldLibrary;
    WorldEntry? activeEntry;
    string? activeSavePath;
    WorldEntry[] worldEntries=[];
    Guid? selectedWorldId;
    readonly MenuText worldSearch=new(),worldNameInput=new(){Text="New World"},worldSeedInput=new(),renameInput=new();
    string frontendStatus="",loadingWorldName="";
    int worldScroll,creationTab,loadingDraws;
    bool exitRequested,preparingWorld;
    Func<WorldLaunch>? pendingWorldLaunch;
    Task<WorldLaunch>? worldLaunchTask;
    Task<string>? backupTask;
    WorldLaunch? preparingLaunch;
    sealed record WorldLaunch(WorldEntry Entry,World World,Vector3 Position,float Yaw,float Pitch,bool Created);
    bool HasPlayableWorld=>startup.HasWorld;
    bool FrontendVisible=>!startup.HasWorld;
    void RefreshWorlds()
    {
        try{
            worldEntries=worldLibrary.List().ToArray();
            if(selectedWorldId is null||!worldEntries.Any(w=>w.Id==selectedWorldId))selectedWorldId=worldEntries.FirstOrDefault()?.Id;
            worldScroll=0;
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){
            worldEntries=[];frontendStatus="Could not read the world list: "+e.Message;
        }
    }
    void ShowSingleplayer()
    {
        frontendStatus="";RefreshWorlds();worldSearch.Text="";lastClickedWorld=null;startup.ShowWorlds(worldEntries.Length==0&&frontendStatus.Length==0);menuTextFocus="";
        if(startup.Screen==StartupScreen.CreateWorld)ResetCreationForm();
    }
    void ResetCreationForm()
    {
        worldNameInput.Text="New World";worldNameInput.SelectAll();worldSeedInput.Text="";
        creationTab=0;classicTerrain=false;newWorldOpen=true;newWorldStatus="";frontendStatus="";menuTextFocus="worldName";
    }
    void ShowCreateScreen()
    {
        startup.ShowCreateWorld();ResetCreationForm();
    }
    void BackFromFrontend()
    {
        if(startup.Screen==StartupScreen.Loading)return;
        if(multiplayerOpen){if(NetworkConnecting)LeaveMultiplayer();CloseMultiplayer();return;}
        if(settingsOpen){CloseSettings();return;}
        startup.Back();newWorldOpen=false;menuTextFocus="";frontendStatus="";
        Raylib.EnableCursor();
    }
    WorldEntry? SelectedWorld()=>worldEntries.FirstOrDefault(w=>w.Id==selectedWorldId);
    void BeginSelectedWorld()
    {
        if(SelectedWorld() is not {CanPlay:true} entry)return;
        var hotbar=palette.Hotbar.ToArray();
        loadingWorldName=entry.Name;
        QueueWorldLaunch(()=>{
            var opened=worldLibrary.Open(entry,hotbar);
            return new(worldLibrary.List().First(w=>w.Id==entry.Id),opened.World,opened.Position,opened.Yaw,opened.Pitch,false);
        });
    }
    static bool TryCreationSeed(string text,out int seed)
    {
        if(string.IsNullOrWhiteSpace(text)){seed=(int)Random.Shared.NextInt64(int.MinValue,(long)int.MaxValue+1);return true;}
        return int.TryParse(text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out seed);
    }
    void BeginCreatedWorld()
    {
        string name=worldNameInput.Text.Trim();
        if(name.Length==0){newWorldStatus="Give your world a name.";return;}
        if(!TryCreationSeed(worldSeedInput.Text,out int seed)){newWorldStatus="Use a seed from -2147483648 to 2147483647, or leave it blank.";return;}
        loadingWorldName=name;
        bool classic=classicTerrain;var hotbar=palette.Hotbar.ToArray();
        QueueWorldLaunch(()=>{
            var created=worldLibrary.Create(name,seed,classic,hotbar);
            return new(created.Entry,created.World,created.World.Spawn(),MathF.PI,-.18f,true);
        });
    }
    void QueueWorldLaunch(Func<WorldLaunch> launch)
    {
        if(worldLaunchTask is not null||pendingWorldLaunch is not null)return;
        startup.BeginLoading();pendingWorldLaunch=launch;loadingDraws=0;
        newWorldOpen=false;frontendStatus="";ResetSimulation();Raylib.EnableCursor();
    }
    void UpdateFrontend()
    {
        if(backupTask?.IsCompleted==true){
            try{frontendStatus="Backup saved. "+Path.GetFileName(backupTask.GetAwaiter().GetResult());}
            catch(Exception e){frontendStatus="Backup failed: "+e.GetBaseException().Message;}
            backupTask=null;
        }
        if(startup.Screen!=StartupScreen.Loading)return;
        if(pendingWorldLaunch is not null&&loadingDraws>0){
            var work=pendingWorldLaunch;pendingWorldLaunch=null;worldLaunchTask=Task.Run(work);
        }
        if(worldLaunchTask?.IsCompleted==true){
            var task=worldLaunchTask;worldLaunchTask=null;
            try{
                var opened=task.GetAwaiter().GetResult();preparingLaunch=opened;
                world=opened.World;activeEntry=opened.Entry;activeSavePath=worldLibrary.GetSavePath(opened.Entry);
                palette.Blocks=world.Registries.Blocks;feet=opened.Position;yaw=opened.Yaw;pitch=opened.Pitch;
                player.VerticalVelocity=0;player.Flying=false;previousFeet=feet;preparingWorld=true;
                starterCircuitPending=opened.Created;SyncHotbar();
            }catch(Exception e){FailWorldLaunch(e);}
        }
        if(preparingWorld){
            try{
                EnsureStreaming();
                if(streamer?.LastError is {} error)throw new IOException("Nearby terrain could not be prepared.",error);
                if(SpawnAreaReady()&&(renderer.CachedChunkCount>0||world.Chunks.Chunks.Count==0)){
                    startup.EnterWorld();preparingWorld=false;preparingLaunch=null;
                    inventoryOpen=settingsOpen=multiplayerOpen=newWorldOpen=paused=false;
                    ResetSimulation();Raylib.DisableCursor();messageTime=4;message=activeEntry!.Name;
                }
            }catch(Exception e){FailWorldLaunch(e);}
        }
    }
    bool SpawnAreaReady()
    {
        int x=(int)MathF.Floor(feet.X),y=(int)MathF.Floor(feet.Y),z=(int)MathF.Floor(feet.Z);
        return world.IsCellAvailable(new(x,y,z))&&world.IsCellAvailable(new(x,y+1,z))&&world.IsCellAvailable(new(x,Math.Max(0,y-1),z));
    }
    void FailWorldLaunch(Exception error)
    {
        StopStreaming(false);preparingWorld=false;preparingLaunch=null;activeEntry=null;activeSavePath=null;
        renderer.ResetWorldCache();world=new World(false,registries:world.Registries);
        startup.FailLoading("Could not open this world: "+error.GetBaseException().Message);
        frontendStatus=startup.Status;newWorldStatus=frontendStatus;
        newWorldOpen=startup.Screen==StartupScreen.CreateWorld;menuTextFocus="";RefreshWorlds();Raylib.EnableCursor();
    }
    bool SaveAndReturnToTitle()
    {
        if(!HasPlayableWorld)return true;
        if(NetworkClientActive){
            LeaveMultiplayer();
            if(HasPlayableWorld&&!TrySave())return false;
        }else{
            if(!TrySave())return false;
            LeaveMultiplayer();
        }
        StopStreaming(false);
        startup.ReturnToTitle();activeEntry=null;activeSavePath=null;
        world=new World(false,registries:world.Registries);
        renderer.ResetWorldCache();inventoryOpen=settingsOpen=newWorldOpen=multiplayerOpen=paused=false;
        ResetSimulation();menuTextFocus="";Raylib.EnableCursor();RefreshWorlds();return true;
    }
    void FinishFrontendJobs()
    {
        pendingWorldLaunch=null;
        if(worldLaunchTask is {} task){try{task.GetAwaiter().GetResult();}catch{}worldLaunchTask=null;}
        if(backupTask is {} backup){try{backup.GetAwaiter().GetResult();}catch{}backupTask=null;}
    }
}
