using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    enum ActionKind { Place, Toggle, Undo, Redo }
    readonly record struct PlayerAction(ActionKind Kind,Cell Cell,Block Block=Block.Air);
    readonly Queue<PlayerAction> actions=[];
    Vector3 moveDirection;float moveSpeed=4,flyVertical;bool jumpPending,mineHeld;
    Vector3 CurrentEye()=>Vector3.Lerp(previousFeet,feet,clock.Alpha)+new Vector3(0,1.62f,0);
    void ResetSimulation()
    {
        clock.ResetAccumulator();previousFeet=feet;actions.Clear();jumpPending=mineHeld=false;
        mining=null;mineTime=0;
    }
    void CaptureInput()
    {
        var mouse=Raylib.GetMouseDelta();yaw-=mouse.X*settings.MouseSensitivity;
        pitch=Math.Clamp(pitch-mouse.Y*settings.MouseSensitivity,-1.5f,1.5f);
        if(Raylib.IsKeyPressed(KeyboardKey.F))
        {player.Flying=!player.Flying;Notice(player.Flying?"Flight ON: SPACE up, CTRL down. F to land.":"Flight OFF: gravity enabled.");}
        var forward=new Vector3(MathF.Sin(yaw),0,MathF.Cos(yaw));var right=new Vector3(-forward.Z,0,forward.X);
        moveDirection=Vector3.Zero;
        if(Raylib.IsKeyDown(KeyboardKey.W))moveDirection+=forward;if(Raylib.IsKeyDown(KeyboardKey.S))moveDirection-=forward;
        if(Raylib.IsKeyDown(KeyboardKey.D))moveDirection+=right;if(Raylib.IsKeyDown(KeyboardKey.A))moveDirection-=right;
        moveSpeed=Raylib.IsKeyDown(KeyboardKey.LeftShift)?6:4;
        bool control=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
        flyVertical=(Raylib.IsKeyDown(KeyboardKey.Space)?1:0)-(control&&!Raylib.IsKeyDown(KeyboardKey.Z)&&!Raylib.IsKeyDown(KeyboardKey.Y)?1:0);
        jumpPending|=Raylib.IsKeyPressed(KeyboardKey.Space);
        mineHeld=Raylib.IsMouseButtonDown(MouseButton.Left);
        for(int i=0;i<8;i++)if(Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One+i)))palette.SelectedSlot=i;
        float wheel=Raylib.GetMouseWheelMove();if(wheel!=0)palette.SelectedSlot=(palette.SelectedSlot+(wheel>0?7:1))%8;
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Z))actions.Enqueue(new(ActionKind.Undo,default));
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Y))actions.Enqueue(new(ActionKind.Redo,default));
        if(world.Raycast(CurrentEye(),Forward) is {} hit)
        {
            if(Raylib.IsKeyPressed(KeyboardKey.E))actions.Enqueue(new(ActionKind.Toggle,hit.Hit));
            if(Raylib.IsMouseButtonPressed(MouseButton.Middle))palette.Assign(world.Get(hit.Hit));
            if(Raylib.IsMouseButtonPressed(MouseButton.Right))actions.Enqueue(new(ActionKind.Place,hit.Previous,palette.Selected));
        }
    }
    bool CanRestore(Cell cell,Block block)=>block is Block.Air or Block.Wire or Block.Lever || !CollisionSystem.OverlapsPlayer(cell,feet);
    void SimulationTick(float dt)
    {
        previousFeet=feet;
        player.Step(world,moveDirection,moveSpeed,jumpPending,flyVertical,dt);jumpPending=false;
        if(Vector3.DistanceSquared(previousFeet,feet)>4)previousFeet=feet;
        while(actions.TryDequeue(out var action))
        {
            switch(action.Kind)
            {
                case ActionKind.Place:
                    if(!CanRestore(action.Cell,action.Block))Notice("Cannot place a block inside the player.");
                    else if(!world.Place(action.Cell,action.Block))Notice("Need inventory, empty space, and support for wire/lever.");
                    break;
                case ActionKind.Toggle:world.Toggle(action.Cell);break;
                case ActionKind.Undo:Notice(world.Undo(CanRestore)?"Edit undone.":"Nothing to undo, or the player blocks restoration.");break;
                case ActionKind.Redo:Notice(world.Redo(CanRestore)?"Edit redone.":"Nothing to redo, or the player blocks restoration.");break;
            }
        }
        if(mineHeld&&world.Raycast(feet+new Vector3(0,1.62f,0),Forward) is {} hit)
        {
            if(mining!=hit.Hit){mining=hit.Hit;mineTime=0;}
            mineTime+=dt;
            if(mineTime>=MineDuration(world.Get(hit.Hit))){world.Mine(hit.Hit);mining=null;mineTime=0;}
        }
        else {mining=null;mineTime=0;}
        if(world.Circuits.PendingEventCount>0)world.Circuits.ProcessPending();
    }
}
