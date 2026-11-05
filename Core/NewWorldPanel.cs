using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    bool newWorldOpen,classicTerrain;
    string newWorldStatus="";
    void OpenNewWorld()
    {
        if(NetworkActive||NetworkConnecting){Notice("Leave multiplayer before creating another world.");return;}
        if(HasPlayableWorld&&!SaveAndReturnToTitle())return;
        if(startup.Screen==StartupScreen.Title)startup.ShowWorlds(false);
        if(startup.Screen==StartupScreen.Worlds)ShowCreateScreen();
    }
    void CloseNewWorld(){newWorldOpen=false;BackFromFrontend();}
    void DrawNewWorldPanel()
    {
        int sw=Raylib.GetScreenWidth(),h=Raylib.GetScreenHeight(),width=Math.Min(700,sw-80),x=(sw-width)/2;
        MenuHeading("Create New World");
        bool ctrl=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
        if(ctrl&&(Raylib.IsKeyPressed(KeyboardKey.Tab)||Raylib.IsKeyPressed(KeyboardKey.One)||Raylib.IsKeyPressed(KeyboardKey.Two))){
            creationTab=Raylib.IsKeyPressed(KeyboardKey.One)?0:Raylib.IsKeyPressed(KeyboardKey.Two)?1:1-creationTab;
            menuTextFocus=creationTab==0?"worldName":"worldSeed";
        }
        int half=(width-12)/2;
        if(MenuButton(new(x,96,half,39),"Game",keyboard:false)){creationTab=0;menuTextFocus="worldName";}
        if(MenuButton(new(x+half+12,96,half,39),"World",keyboard:false)){creationTab=1;menuTextFocus="worldSeed";}
        Raylib.DrawRectangle(x+creationTab*(half+12),134,half,3,Color.White);
        Raylib.DrawRectangle(x-18,151,width+36,h-285,new Color(16,20,26,170));
        if(creationTab==0){
            Raylib.DrawText("World Name",x,180,18,Color.White);
            MenuTextField("worldName",new(x,211,width,43),worldNameInput,64);
            Raylib.DrawText("Game Mode: Sandbox",x,289,21,Color.White);
            DrawTextFit("Build, mine, craft and explore. Double Space lets you fly.",x,326,16,width,new Color(202,209,215,255));
            DrawTextFit("Each world keeps its own terrain, inventory and progress.",x,370,16,width,new Color(202,209,215,255));
        }else{
            Raylib.DrawText("Seed for World Generation",x,180,18,Color.White);
            MenuTextField("worldSeed",new(x,211,width-132,43),worldSeedInput,32,"Leave blank for a random seed");
            if(MenuButton(new(x+width-120,211,120,43),"Random")){worldSeedInput.Text=((int)Random.Shared.NextInt64(int.MinValue,(long)int.MaxValue+1)).ToString();menuTextFocus="worldSeed";}
            DrawTextFit("Use the same seed and terrain type to start from the same landscape.",x,270,15,width,new Color(202,209,215,255));
            Raylib.DrawText("World Type",x,316,18,Color.White);
            if(MenuButton(new(x,350,half,42),"Natural"))classicTerrain=false;
            if(MenuButton(new(x+half+12,350,half,42),"Classic"))classicTerrain=true;
            Raylib.DrawRectangleLinesEx(new(x+(classicTerrain?half+12:0),350,half,42),2,new Color(245,232,166,255));
            DrawTextFit(classicTerrain?"Original starting terrain, with new land beyond it.":"Connected hills, caves, trees and ores.",x,409,16,width,new Color(202,209,215,255));
        }
        bool validSeed=string.IsNullOrWhiteSpace(worldSeedInput.Text)||int.TryParse(worldSeedInput.Text,out _);
        string status=newWorldStatus.Length>0?newWorldStatus:frontendStatus;
        if(!validSeed)status="Seed must be a whole number from -2147483648 to 2147483647.";
        DrawTextFit(status,x,h-149,14,width,new Color(245,181,153,255));
        bool validName=!string.IsNullOrWhiteSpace(worldNameInput.Text);
        if(MenuButton(new(x,h-105,half,43),"Create New World",validName&&validSeed))BeginCreatedWorld();
        if(MenuButton(new(x+half+12,h-105,half,43),"Cancel"))CloseNewWorld();
        if(Raylib.IsKeyPressed(KeyboardKey.Enter)&&validName&&validSeed)BeginCreatedWorld();
        MenuCentered("Ctrl+Tab switches tabs",h-39,13,new Color(202,209,215,255));
    }
}
