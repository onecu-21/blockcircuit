using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    void DrawMenuBackground()
    {
        int w=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight();
        Raylib.DrawRectangleGradientV(0,0,w,h,new Color(102,163,192,255),new Color(209,224,210,255));
        float drift=(float)Math.Sin(Raylib.GetTime()*.035)*20;
        Raylib.DrawCircle(w-175,116,43,new Color(246,232,174,255));
        for(int layer=0;layer<3;layer++){
            int unit=layer==0?180:layer==1?135:100;int baseY=(int)(h*(.45f+layer*.15f));
            var color=layer==0?new Color(116,153,151,255):layer==1?new Color(78,120,112,255):new Color(45,82,70,255);
            for(int x=-unit;x<w+unit;x+=unit){
                int top=baseY-(int)(Math.Sin((x+layer*390)*.008)*55)-unit/2;
                int bx=x+(int)(drift*(layer+1));
                Raylib.DrawRectangle(bx,top,unit+1,h-top,color);
                if(layer==2){
                    Raylib.DrawRectangle(bx,top,unit+1,10,new Color(71,108,69,255));
                    if((x/unit&1)==0){
                        Raylib.DrawRectangle(bx+40,top-54,12,54,new Color(60,57,43,255));
                        Raylib.DrawRectangle(bx+17,top-85,58,34,new Color(38,73,53,255));
                    }
                }
            }
        }
        Raylib.DrawRectangle(0,0,w,h,new Color(8,15,24,85));
    }
    void RenderFrontend()
    {
        int w=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight();
        bool drawSettings=settingsOpen,drawMultiplayer=multiplayerOpen;
        Raylib.BeginDrawing();
        if(preparingWorld){
            var camera=BuildCamera(CurrentEye());Raylib.BeginMode3D(camera);
            renderer.DrawWorld(world,camera,(float)w/h);Raylib.EndMode3D();
        }
        DrawMenuBackground();
        switch(startup.Screen){
            case StartupScreen.Title:DrawTitleScreen();break;
            case StartupScreen.Worlds:DrawWorldList();break;
            case StartupScreen.CreateWorld:DrawNewWorldPanel();break;
            case StartupScreen.RenameWorld:DrawEditWorld();break;
            case StartupScreen.DeleteWorld:DrawDeleteWorld();break;
            case StartupScreen.Loading:DrawLoadingWorld();break;
        }
        // An opening click belongs to the parent screen, so the overlay starts next frame.
        if(drawSettings&&settingsOpen)DrawSettingsPanel();
        if(drawMultiplayer&&multiplayerOpen)DrawMultiplayerPanel();
        Raylib.EndDrawing();
        if(startup.Screen==StartupScreen.Loading)loadingDraws++;
    }
    void DrawTitleScreen()
    {
        int w=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight(),cx=w/2,top=Math.Max(220,h/2-40);
        string title="BLOCK CIRCUIT";int size=Math.Min(64,(w-160)/10);
        int titleX=(w-Raylib.MeasureText(title,size))/2;
        Raylib.DrawText(title,titleX+4,94,size,new Color(22,29,31,255));
        Raylib.DrawText(title,titleX,88,size,new Color(225,229,221,255));
        MenuCentered("BUILD  /  EXPLORE  /  CONNECT",163,17,new Color(247,231,154,255));
        if(settingsOpen||multiplayerOpen)return;
        if(Raylib.IsKeyPressed(KeyboardKey.Tab)||Raylib.IsKeyPressed(KeyboardKey.Down))titleChoice=(titleChoice+1)%4;
        if(Raylib.IsKeyPressed(KeyboardKey.Up))titleChoice=(titleChoice+3)%4;
        if(MenuButton(new(cx-210,top,420,44),"Singleplayer",keyboard:titleChoice==0))ShowSingleplayer();
        if(MenuButton(new(cx-210,top+56,420,44),"Multiplayer",keyboard:titleChoice==1))OpenMultiplayer();
        if(MenuButton(new(cx-210,top+126,202,42),"Options...",keyboard:titleChoice==2))OpenSettings();
        if(MenuButton(new(cx+8,top+126,202,42),"Quit Game",keyboard:titleChoice==3))exitRequested=true;
        Raylib.DrawText("Block Circuit | Sandbox",18,h-30,14,new Color(220,224,222,255));
        string note="Your worlds. Your way.";Raylib.DrawText(note,w-Raylib.MeasureText(note,14)-18,h-30,14,new Color(220,224,222,255));
    }
    WorldEntry[] FilteredWorlds()=>worldEntries.Where(e=>e.Name.Contains(worldSearch.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
    void DrawWorldList()
    {
        int w=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight(),width=Math.Min(850,w-80),x=(w-width)/2;
        MenuHeading("Select World");
        MenuTextField("worldSearch",new(x,88,width,36),worldSearch,64,"Search worlds");
        var rows=FilteredWorlds();
        var list=new Rectangle(x,140,width,h-300);
        Raylib.DrawRectangleRec(list,new Color(12,15,20,160));
        int visible=Math.Max(1,(int)list.Height/78);
        bool inside=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),list);
        if(inside&&Raylib.GetMouseWheelMove()!=0)worldScroll+=(Raylib.GetMouseWheelMove()<0?1:-1);
        worldScroll=Math.Clamp(worldScroll,0,Math.Max(0,rows.Length-visible));
        if(rows.Length>0&&(Raylib.IsKeyPressed(KeyboardKey.Up)||Raylib.IsKeyPressed(KeyboardKey.Down))){
            int current=Array.FindIndex(rows,r=>r.Id==selectedWorldId);
            current=Math.Clamp(current+(Raylib.IsKeyPressed(KeyboardKey.Down)?1:-1),0,rows.Length-1);
            selectedWorldId=rows[current].Id;worldScroll=Math.Clamp(current-visible+1,0,current);menuTextFocus="";
        }
        Raylib.BeginScissorMode(x,(int)list.Y,width,(int)list.Height);
        for(int i=worldScroll;i<rows.Length&&i<worldScroll+visible;i++){
            var entry=rows[i];int y=(int)list.Y+(i-worldScroll)*78;
            var box=new Rectangle(x+2,y+2,width-4,74);bool hover=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),box);
            if(selectedWorldId==entry.Id){Raylib.DrawRectangleRec(box,new Color(70,73,79,200));Raylib.DrawRectangleLinesEx(box,2,Color.White);}
            else if(hover)Raylib.DrawRectangleRec(box,new Color(48,52,58,190));
            DrawWorldGlyph(x+12,y+12,52,entry.Classic);
            DrawTextFit(entry.Name,x+82,y+11,21,width-100,entry.CanPlay?Color.White:new Color(180,182,187,255));
            string mode=entry.Classic?"Sandbox | Classic starting area":"Sandbox | Natural terrain";
            string last=entry.LastPlayedUtc==DateTime.UnixEpoch?"Not played yet":entry.LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            DrawTextFit(mode+"  |  "+last,x+82,y+37,14,width-100,new Color(190,195,200,255));
            DrawTextFit(entry.Error??(entry.Legacy?"Existing save":$"Seed: {entry.Seed}"),x+82,y+55,12,width-100,entry.Error is null?new Color(150,159,165,255):new Color(235,174,130,255));
            if(hover&&Raylib.IsMouseButtonPressed(MouseButton.Left)){
                bool twice=lastClickedWorld==entry.Id&&lastMenuClick>0&&Raylib.GetTime()-lastMenuClick<.35;
                selectedWorldId=entry.Id;menuTextFocus="";lastMenuClick=Raylib.GetTime();lastClickedWorld=entry.Id;
                if(twice&&entry.CanPlay)BeginSelectedWorld();
            }
        }
        Raylib.EndScissorMode();
        if(rows.Length==0){
            MenuCentered(worldEntries.Length==0?"No worlds yet. Create your first world below.":"No matching worlds.",(int)list.Y+65,19,Color.White);
        }
        var chosen=rows.FirstOrDefault(r=>r.Id==selectedWorldId);
        if(menuTextFocus==""&&Raylib.IsKeyPressed(KeyboardKey.Enter)&&chosen?.CanPlay==true)BeginSelectedWorld();
        int by=h-137;
        if(MenuButton(new(x,by,width/2-6,40),"Play Selected World",chosen?.CanPlay==true))BeginSelectedWorld();
        if(MenuButton(new(x+width/2+6,by,width/2-6,40),"Create New World"))ShowCreateScreen();
        int third=(width-24)/3;
        if(MenuButton(new(x,by+52,third,38),"Edit",chosen is not null)){
            renameInput.Text=chosen!.Name;renameInput.SelectAll();menuTextFocus="rename";frontendStatus="";startup.ShowRename();
        }
        if(MenuButton(new(x+third+12,by+52,third,38),"Delete",chosen is not null)){frontendStatus="";startup.ShowDelete();}
        if(MenuButton(new(x+2*(third+12),by+52,third,38),"Back"))BackFromFrontend();
        DrawTextFit(frontendStatus,x,h-176,14,width,new Color(245,181,153,255));
    }
    void DrawWorldGlyph(int x,int y,int size,bool classic)
    {
        Raylib.DrawRectangle(x,y,size,size,new Color(116,158,176,255));
        Raylib.DrawRectangle(x,y+size/2,size,size/2,new Color(103,78,54,255));
        Raylib.DrawRectangle(x,y+size/2,size,8,new Color(98,143,66,255));
        if(!classic){Raylib.DrawRectangle(x+size/2,y+size/3,size/2,size*2/3,new Color(102,105,103,255));Raylib.DrawRectangle(x+size/2,y+size/3,size/2,6,new Color(111,145,81,255));}
        Raylib.DrawRectangleLines(x,y,size,size,new Color(32,37,42,255));
    }
    void DrawEditWorld()
    {
        var entry=SelectedWorld();if(entry is null){startup.Back();return;}
        int w=Math.Min(620,Raylib.GetScreenWidth()-80),x=(Raylib.GetScreenWidth()-w)/2,h=Raylib.GetScreenHeight();
        MenuHeading("Edit World",entry.Name);
        Raylib.DrawText("World Name",x,151,18,Color.White);
        MenuTextField("rename",new(x,181,w,42),renameInput,64);
        if(MenuButton(new(x,248,w,42),"Save Name",backupTask is null&&!string.IsNullOrWhiteSpace(renameInput.Text))){
            try{worldLibrary.Rename(entry.Id,renameInput.Text);RefreshWorlds();startup.Back();menuTextFocus="";}
            catch(Exception e){frontendStatus="Could not rename this world: "+e.Message;}
        }
        if(MenuButton(new(x,306,w,42),backupTask is null?"Make Backup":"Making backup...",backupTask is null&&entry.CanPlay)){
            backupTask=Task.Run(()=>worldLibrary.Backup(entry.Id));frontendStatus="Making a recovery copy...";
        }
        DrawTextFit("Backups keep this world's blocks, inventory and player progress.",x,366,15,w,new Color(207,212,218,255));
        DrawTextFit(frontendStatus,x,405,15,w,new Color(245,181,153,255));
        if(MenuButton(new(x,h-82,w,40),"Back",backupTask is null))BackFromFrontend();
    }
    void DrawDeleteWorld()
    {
        var entry=SelectedWorld();if(entry is null){startup.Back();return;}
        int w=Math.Min(680,Raylib.GetScreenWidth()-80),x=(Raylib.GetScreenWidth()-w)/2,h=Raylib.GetScreenHeight();
        MenuHeading("Delete World?");
        MenuCentered(entry.Name,176,25,Color.White);
        MenuCentered("This world will be removed from the list.",225,18,new Color(226,228,231,255));
        MenuCentered("A recovery copy is kept in Trash.",260,16,new Color(203,208,214,255));
        DrawTextFit(frontendStatus,x,315,15,w,new Color(245,181,153,255));
        if(MenuButton(new(x,h-118,w/2-8,42),"Delete World")){
            try{worldLibrary.Trash(entry.Id);RefreshWorlds();startup.Back();frontendStatus="World moved to Trash.";}
            catch(Exception e){frontendStatus="Could not delete this world: "+e.Message;}
        }
        if(MenuButton(new(x+w/2+8,h-118,w/2-8,42),"Cancel"))BackFromFrontend();
    }
    void DrawLoadingWorld()
    {
        string dots=new('.',(int)(Raylib.GetTime()*2)%4);
        MenuHeading("Loading World"+dots,preparingLaunch?.Entry.Name??loadingWorldName);
        string stage=preparingWorld?(SpawnAreaReady()?"Building the view...":"Preparing the spawn area..."):"Reading world data...";
        MenuCentered(stage,Raylib.GetScreenHeight()/2-20,21,Color.White);
        int size=44,x=Raylib.GetScreenWidth()/2-size/2,y=Raylib.GetScreenHeight()/2+25;
        DrawWorldGlyph(x,y,size,preparingLaunch?.Entry.Classic??classicTerrain);
        MenuCentered("Please wait",Raylib.GetScreenHeight()-95,15,new Color(214,218,222,255));
    }
    void DrawPauseMenu()
    {
        int w=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight(),x=w/2-200,y=h/2-155;
        Raylib.DrawRectangle(0,0,w,h,new Color(0,0,0,150));
        MenuCentered("Game Menu",y-53,28,Color.White);
        if(MenuButton(new(x,y,400,42),"Back to Game")){paused=false;ResetSimulation();Raylib.DisableCursor();}
        if(MenuButton(new(x,y+57,194,40),"Options..."))OpenSettings();
        if(MenuButton(new(x+206,y+57,194,40),NetworkActive?"Multiplayer...":"Open to LAN..."))OpenMultiplayer();
        if(MenuButton(new(x,y+111,194,40),"Save World",!NetworkClientActive))TrySave();
        if(MenuButton(new(x+206,y+111,194,40),"Create New World",!NetworkActive))OpenNewWorld();
        if(MenuButton(new(x,y+178,400,42),NetworkClientActive?"Disconnect":"Save and Quit to Title"))SaveAndReturnToTitle();
        if(NetworkActive)MenuCentered("The shared world keeps running.",y+238,14,new Color(230,215,166,255));
        if(frontendStatus.Length>0)DrawTextFit(frontendStatus,x,y+267,14,400,new Color(245,181,153,255));
    }
}
