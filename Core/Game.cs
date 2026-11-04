using System.Numerics;
using System.Diagnostics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    World world;
    readonly PlayerBody player=new();
    readonly GameClock clock=new();
    readonly GameSettings settings;
    Vector3 previousFeet;
    Vector3 feet {get=>player.Position;set=>player.Position=value;}
    float yaw=MathF.PI,pitch=-.18f;bool paused;
    BlockRenderer renderer=null!;
    bool debugOverlay,circuitDebug,hudHidden;double rendererCpuMilliseconds;
    Cell? mining;float mineTime;string message="E: inventory  |  Double SPACE: fly  |  F5: perspective";float messageTime=8;
    readonly string dataDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolderOption.Create),"BlockCircuit");
    string savePath=>Path.Combine(dataDirectory,"world.json");
    string settingsPath=>Path.Combine(dataDirectory,"settings.json");
    string logPath=>Path.Combine(dataDirectory,"game.log");
    readonly Color ink=new(22,29,40,255),accent=new(87,231,182,255);
    public Game(bool smoke=false,bool terrainPreview=false,GameRegistries? registries=null)
    {
        world=smoke&&!terrainPreview?new World(registries:registries):new World(seed:2026,legacyTerrain:false,registries:registries);
        palette.Blocks=world.Registries.Blocks;
        settings=smoke?new GameSettings():GameSettings.Load(settingsPath);
        settings.ApplyToPalette(palette);
        feet=world.Spawn();previousFeet=feet;
        if(!smoke&&File.Exists(savePath))TryLoad();
        if(!smoke&&settings.LoadWarning is {} warning){Notice(warning);GameLog.Write(logPath,"settings",warning);}
    }
    Vector3 Forward=>Vector3.Normalize(new(MathF.Sin(yaw)*MathF.Cos(pitch),MathF.Sin(pitch),MathF.Cos(yaw)*MathF.Cos(pitch)));
    void Notice(string text){message=text;messageTime=5;}
    public void Run(bool smoke,bool paletteSmoke=false,bool showcaseSmoke=false,bool engineSmoke=false,bool gameplaySmoke=false,bool terrainSmoke=false,bool controlsSmoke=false)
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow|ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(1280,800,"Block Circuit | build, mine, connect");
        Raylib.SetWindowMinSize(960,640);Raylib.SetTargetFPS(smoke?60:settings.TargetFps);Raylib.SetExitKey(KeyboardKey.Null);Raylib.DisableCursor();
        renderer=new BlockRenderer{DrawDistance=settings.DrawDistance};
        if(paletteSmoke)ToggleInventory();
        if(showcaseSmoke)SetupShowcase();
        if(terrainSmoke){feet=new(34,24,36);previousFeet=feet;yaw=-2.42f;pitch=-.46f;hudHidden=true;}
        int frames=0;
        using var verifier=engineSmoke?new EngineSmokeVerifier():null;
        if(engineSmoke)debugOverlay=true;
        try
        {
            while(!Raylib.WindowShouldClose())
            {
                verifier?.BeforeFrame(world,frames);
                if(gameplaySmoke)AdvanceGameplaySmoke(frames);
                if(controlsSmoke)AdvanceControlsSmoke(frames);
                if(Raylib.IsKeyPressed(KeyboardKey.F1))hudHidden=!hudHidden;
                if(Raylib.IsKeyPressed(KeyboardKey.F3))debugOverlay=!debugOverlay;
                if(Raylib.IsKeyPressed(KeyboardKey.F4))circuitDebug=!circuitDebug;
                double frameSeconds=Raylib.GetFrameTime();if(!double.IsFinite(frameSeconds)||frameSeconds<0)frameSeconds=0;messageTime-=(float)Math.Min(frameSeconds,.25);
                bool uiToggled=false;
                bool control=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
                bool screenshotRequested=!smoke&&Raylib.IsKeyPressed(KeyboardKey.F2);
                if(Raylib.IsKeyPressed(KeyboardKey.Escape))
                {
                    if(newWorldOpen)CloseNewWorld();
                    else if(settingsOpen)CloseSettings();
                    else if(inventoryOpen)ToggleInventory();
                    else {paused=!paused;if(paused)Raylib.EnableCursor();else Raylib.DisableCursor();}
                    uiToggled=true;
                }
                if(!newWorldOpen&&Raylib.IsKeyPressed(KeyboardKey.F10))
                {if(settingsOpen)CloseSettings();else OpenSettings();uiToggled=true;}
                if(!smoke&&Raylib.IsKeyPressed(KeyboardKey.F6)){if(newWorldOpen)CloseNewWorld();else OpenNewWorld();uiToggled=true;}
                if(!paused&&!settingsOpen&&!newWorldOpen&&(!inventoryOpen||!searchFocused)&&Raylib.IsKeyPressed(KeyboardKey.E)){ToggleInventory();uiToggled=true;}
                if(!inventoryOpen&&!settingsOpen&&!newWorldOpen&&Raylib.IsKeyPressed(KeyboardKey.F5)){CyclePerspective();uiToggled=true;}
                if(!smoke&&!inventoryOpen&&!settingsOpen&&!newWorldOpen&&control)
                {
                    if(Raylib.IsKeyPressed(KeyboardKey.S)){TrySave();uiToggled=true;}
                    if(Raylib.IsKeyPressed(KeyboardKey.O)){TryLoad();uiToggled=true;}
                }
                if(!paused&&!inventoryOpen&&!settingsOpen&&!newWorldOpen&&!showcaseSmoke&&!engineSmoke&&!gameplaySmoke&&!terrainSmoke&&!controlsSmoke&&!uiToggled)
                {
                    CaptureInput();
                    clock.Advance(frameSeconds,SimulationTick);
                }
                else if(gameplaySmoke&&!settingsOpen){clock.Advance(1d/60,SimulationTick);CheckGameplaySmoke(frames);}
                else {ResetSimulation();if(gameplaySmoke)CheckGameplaySmoke(frames);}
                Render(CurrentEye());
                if(screenshotRequested)TryScreenshot();
                verifier?.AfterFrame(world,renderer,frames);
                frames++;
                int limit=engineSmoke?EngineSmokeVerifier.FrameCount:gameplaySmoke?90:controlsSmoke?60:30;
                if(smoke&&frames==limit)
                {
                    Raylib.TakeScreenshot(controlsSmoke?"controls-preview.png":terrainSmoke?"terrain-preview.png":gameplaySmoke?"gameplay-preview.png":engineSmoke?"engine-preview.png":paletteSmoke?"palette-preview.png":showcaseSmoke?"blocks-preview.png":"smoke-test.png");break;
                }
            }
            if(!smoke)TrySave();
        }
        finally{renderer.Dispose();Raylib.CloseWindow();}
        verifier?.Complete(renderer);
        if(gameplaySmoke)FinishGameplaySmoke();
        if(controlsSmoke)FinishControlsSmoke();
    }
    float MineDuration(Block b)=>world.Registries.Blocks.GetInfo(b).Hardness;
    void TrySave()
    {
        try
        {
            world.Save(savePath,feet,yaw,pitch);settings.CapturePalette(palette);settings.Save(settingsPath);
            Notice("World and hotbar saved.");GameLog.Write(logPath,"save","World and settings saved.");
        }
        catch(Exception e){Notice("Save failed: "+e.Message);GameLog.Write(logPath,"save-error",e.Message);}
    }
    void TryLoad()
    {
        try
        {
            var state=world.Load(savePath);feet=state.Position;yaw=state.Yaw;pitch=state.Pitch;
            player.VerticalVelocity=0;player.Flying=false;ResetSimulation();
            Notice(world.LastLoadRecovered?"Recovered the previous saved world. Edit history cleared.":"World loaded. Edit history cleared.");
            if(world.LastLoadRecovered)GameLog.Write(logPath,"load-recovery","Recovered the previous valid saved world.");
        }
        catch(Exception e){Notice("Load failed: "+e.Message);GameLog.Write(logPath,"load-error",e.Message);}
    }
    void SetupShowcase()
    {
        world=new World(false);
        for(int x=0;x<World.Size;x++)for(int z=0;z<World.Size;z++)world.Set(new(x,0,z),Block.Stone);
        int index=0;
        foreach(var block in world.Registries.Blocks.Placeable)
        {
            int x=3+(index%17)*2,z=3+(index/17)*2;world.Set(new(x,1,z),block.Id);index++;
        }
        feet=new(20,21,37);previousFeet=feet;yaw=MathF.PI;pitch=-.75f;
    }
    void Render(Vector3 eye)
    {
        int width=Raylib.GetScreenWidth(),height=Raylib.GetScreenHeight();
        var camera=BuildCamera(eye);
        var target=world.Raycast(eye,Forward);
        Raylib.BeginDrawing();Raylib.ClearBackground(new Color(171,201,214,255));renderer.DrawSky(camera,width,height);Raylib.BeginMode3D(camera);
        long renderStart=Stopwatch.GetTimestamp();
        renderer.DrawWorld(world,camera,(float)width/height);
        DrawPlayerAvatar(Vector3.Lerp(previousFeet,feet,clock.Alpha));
        rendererCpuMilliseconds=Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
        if(circuitDebug)DrawCircuitLines(eye);
        if(!hudHidden&&target is {} hit)Raylib.DrawCubeWires(hit.Hit.Center,1.015f,1.015f,1.015f,Color.White);
        Raylib.EndMode3D();
        if(hudHidden&&!paused&&!inventoryOpen&&!settingsOpen&&!newWorldOpen){Raylib.EndDrawing();return;}
        Raylib.DrawRectangle(20,20,305,86,ink);Raylib.DrawText("BLOCK / CIRCUIT",36,32,25,accent);
        Raylib.DrawText($"{world.Registries.Blocks.Placeable.Count()} BLOCKS  /  BUILD AND MINE",36,66,14,new Color(165,184,195,255));Raylib.DrawText($"{(int)feet.X}, {(int)feet.Y}, {(int)feet.Z}   |   {Raylib.GetFPS()} FPS",36,86,12,Color.White);
        Raylib.DrawRectangle(width-382,20,362,160,ink);
        Raylib.DrawText("WASD move  CTRL sprint  SHIFT sneak",width-366,35,13,Color.White);
        Raylib.DrawText("SPACE jump  double SPACE fly",width-366,57,13,Color.White);
        Raylib.DrawText("LMB mine  RMB use/place  E inventory",width-366,79,13,Color.White);
        Raylib.DrawText("1-9 slots  MMB pick  F5 perspective",width-366,101,13,accent);
        Raylib.DrawText("F1 HUD  F2 screenshot  F3/F4 debug",width-366,123,13,accent);
        Raylib.DrawText("CTRL+S/O save/load  F6 world  F10 settings",width-366,145,12,accent);
        int cx=width/2,cy=height/2;Raylib.DrawLine(cx-7,cy,cx+7,cy,Color.White);Raylib.DrawLine(cx,cy-7,cx,cy+7,Color.White);
        if(target is {} t)
        {
            var b=world.Get(t.Hit);string text=world.Registries.Blocks.GetInfo(b).Name+(b is Block.Wire or Block.Lever or Block.Lamp?$"  |  Power {world.Power.GetValueOrDefault(t.Hit)}":"");
            Raylib.DrawText(text,cx-Raylib.MeasureText(text,18)/2,cy+28,18,ink);
            if(mining!=null){Raylib.DrawRectangle(cx-60,cy+52,120,6,ink);Raylib.DrawRectangle(cx-60,cy+52,(int)(120*Math.Clamp(mineTime/MineDuration(b),0,1)),6,accent);}
        }
        int start=cx-palette.Hotbar.Length*82/2;
        for(int i=0;i<palette.Hotbar.Length;i++)
        {
            int x=start+i*82,y=height-92;var b=palette.Hotbar[i];
            Raylib.DrawRectangle(x,y,76,72,i==palette.SelectedSlot?accent:ink);Raylib.DrawRectangle(x+3,y+3,70,66,ink);
            renderer.DrawIcon(b,x+23,y+9,32,world.Registries.Blocks);Raylib.DrawText($"{i+1}",x+7,y+7,12,Color.White);
            var words=world.Registries.Blocks.GetInfo(b).Name.Split(' ');string shortName=words.Length>1?words[^1]:words[0];
            DrawTextFit(shortName,x+7,y+43,12,62,Color.White);
            Raylib.DrawText(world.Inventory[(int)b].ToString(),x+26,y+57,11,accent);
        }
        string selectedName=world.Registries.Blocks.GetInfo(palette.Selected).Name+(player.Flying?"  /  FLYING":"");
        Raylib.DrawText(selectedName,cx-Raylib.MeasureText(selectedName,18)/2,height-117,18,ink);
        if(messageTime>0){int tw=Raylib.MeasureText(message,17);Raylib.DrawRectangle(cx-tw/2-14,height-166,tw+28,32,ink);Raylib.DrawText(message,cx-tw/2,height-158,17,Color.White);}
        if(paused)
        {
            Raylib.DrawRectangle(0,0,width,height,new Color(12,20,30,170));Raylib.DrawText("PAUSED",cx-83,cy-65,40,accent);
            string text="ESC resume  |  F10 settings  |  CTRL+S save  |  CTRL+O load";Raylib.DrawText(text,cx-Raylib.MeasureText(text,20)/2,cy,20,Color.White);
        }
        if(debugOverlay)DrawEngineOverlay();
        if(circuitDebug)DrawCircuitPanel(target);
        if(inventoryOpen&&!settingsOpen)DrawPalette();
        if(settingsOpen)DrawSettingsPanel();
        if(newWorldOpen)DrawNewWorldPanel();
        Raylib.EndDrawing();
    }
}
