using System.Numerics;
using System.Text.Json;
using BlockCircuit;

sealed partial class Game
{
    readonly List<string> gameplayChecks=[];
    int gameplayStock;float flightStartY;
    void AdvanceGameplaySmoke(int frame)
    {
        switch(frame)
        {
            case 0: circuitDebug=true;gameplayStock=world.Inventory[(int)Block.RedWool];break;
            case 20:
                palette.SelectedSlot=7;palette.Assign(Block.RedWool);
                actions.Enqueue(new(ActionKind.Place,new(20,10,20),Block.RedWool));break;
            case 25:actions.Enqueue(new(ActionKind.Undo,default));break;
            case 30:actions.Enqueue(new(ActionKind.Redo,default));break;
            case 40:actions.Enqueue(new(ActionKind.Toggle,new(18,10,18)));break;
            case 45:actions.Enqueue(new(ActionKind.Undo,default));break;
            case 50:actions.Enqueue(new(ActionKind.Redo,default));break;
            case 55:player.Flying=true;flightStartY=feet.Y;moveDirection=Vector3.UnitX;flyVertical=1;break;
            case 70:moveDirection=Vector3.Zero;flyVertical=0;break;
            case 75:OpenSettings();break;
        }
    }
    void CheckGameplaySmoke(int frame)
    {
        switch(frame)
        {
            case 20:GameplayRequire(world.Get(new(20,10,20))==Block.RedWool&&world.Inventory[(int)Block.RedWool]==gameplayStock-1&&world.UndoCount==1,"fixed-tick placement consumes stock and records history");break;
            case 25:GameplayRequire(world.Get(new(20,10,20))==Block.Air&&world.Inventory[(int)Block.RedWool]==gameplayStock&&world.RedoCount==1,"queued undo restores block and inventory");break;
            case 30:GameplayRequire(world.Get(new(20,10,20))==Block.RedWool&&world.Inventory[(int)Block.RedWool]==gameplayStock-1,"queued redo reapplies construction");break;
            case 40:GameplayRequire(world.Power.GetValueOrDefault(new(24,10,18))>0,"fixed-tick lever updates lamp through cached circuit");break;
            case 45:GameplayRequire(world.Power.GetValueOrDefault(new(24,10,18))==0,"undo switches circuit off");break;
            case 50:GameplayRequire(world.Power.GetValueOrDefault(new(24,10,18))>0,"redo restores circuit power");break;
            case 70:GameplayRequire(feet.Y>flightStartY+.5f&&!CollisionSystem.Collides(world,feet),"flight rises with collision enabled");break;
            case 89:GameplayRequire(settingsOpen&&clock.TotalTicks==75,"settings pause the fixed simulation without catchup");break;
        }
    }
    void GameplayRequire(bool condition,string text)
    {if(!condition)throw new InvalidOperationException("Gameplay smoke failed: "+text);gameplayChecks.Add(text);Console.WriteLine("PASS: "+text);}
    void FinishGameplaySmoke()
    {
        string path=Path.Combine(Path.GetTempPath(),"blockcircuit-settings-"+Guid.NewGuid()+".json");
        try
        {
            settings.CapturePalette(palette);settings.Save(path);var loaded=GameSettings.Load(path);
            GameplayRequire(loaded.Hotbar[7]==Block.RedWool&&loaded.SelectedSlot==7,"settings retain the chosen hotbar across reload");
        }
        finally{File.Delete(path);File.Delete(path+".tmp");}
        File.WriteAllText("gameplay-smoke-results.json",JsonSerializer.Serialize(new{Passed=true,Frames=90,
            FixedTicks=clock.TotalTicks,Checks=gameplayChecks,NetworkCount=world.Circuits.NetworkCount,
            UndoCount=world.UndoCount,RedoCount=world.RedoCount},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS: {gameplayChecks.Count} gameplay integration checks in a real graphics context.");
    }
}
