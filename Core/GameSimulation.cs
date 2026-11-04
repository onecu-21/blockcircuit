using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    enum ActionKind { Place, Toggle, Undo, Redo }
    readonly record struct PlayerAction(ActionKind Kind,Cell Cell,Block Block=Block.Air);
    readonly Queue<PlayerAction> actions=[];
    readonly PlayerControls controls=new();
    Vector3 moveDirection;float moveSpeed=4,flyVertical;bool jumpPending,jumpHeld,mineHeld,sneakHeld;
    Vector3 CurrentEye()=>Vector3.Lerp(previousFeet,feet,clock.Alpha)+new Vector3(0,player.EyeHeight,0);
    void ResetSimulation()
    {
        clock.ResetAccumulator();previousFeet=feet;actions.Clear();jumpPending=jumpHeld=mineHeld=sneakHeld=false;controls.Reset();
        mining=null;mineTime=0;
    }
    void CaptureInput()
    {
        var mouse=Raylib.GetMouseDelta();yaw-=mouse.X*settings.MouseSensitivity;
        pitch=Math.Clamp(pitch-mouse.Y*settings.MouseSensitivity,-1.5f,1.5f);
        bool jumpPressed=Raylib.IsKeyPressed(KeyboardKey.Space);
        jumpHeld=Raylib.IsKeyDown(KeyboardKey.Space);
        sneakHeld=Raylib.IsKeyDown(KeyboardKey.LeftShift);
        bool forwardHeld=Raylib.IsKeyDown(KeyboardKey.W)&&!Raylib.IsKeyDown(KeyboardKey.S);
        if(controls.Sample(Raylib.GetTime(),jumpPressed,Raylib.IsKeyPressed(KeyboardKey.W),forwardHeld,
            Raylib.IsKeyDown(KeyboardKey.LeftControl),sneakHeld))
        {
            player.Flying=!player.Flying;jumpPending=false;jumpPressed=false;
            Notice(player.Flying?"Flight ON: SPACE up, SHIFT down. Double SPACE to land.":"Flight OFF: gravity enabled.");
        }
        var forward=new Vector3(MathF.Sin(yaw),0,MathF.Cos(yaw));var right=new Vector3(-forward.Z,0,forward.X);
        moveDirection=Vector3.Zero;
        if(Raylib.IsKeyDown(KeyboardKey.W))moveDirection+=forward;if(Raylib.IsKeyDown(KeyboardKey.S))moveDirection-=forward;
        if(Raylib.IsKeyDown(KeyboardKey.D))moveDirection+=right;if(Raylib.IsKeyDown(KeyboardKey.A))moveDirection-=right;
        moveSpeed=(sneakHeld||player.Crouching)&&!player.Flying?1.3f:controls.Sprinting?6:4;
        bool control=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
        flyVertical=(jumpHeld?1:0)-(sneakHeld?1:0);
        jumpPending|=jumpPressed;
        mineHeld=Raylib.IsMouseButtonDown(MouseButton.Left);
        for(int i=0;i<palette.Hotbar.Length;i++)if(Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One+i)))palette.SelectedSlot=i;
        palette.SelectedSlot=PlayerControls.ScrollSlot(palette.SelectedSlot,Raylib.GetMouseWheelMove(),palette.Hotbar.Length);
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Z))actions.Enqueue(new(ActionKind.Undo,default));
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Y))actions.Enqueue(new(ActionKind.Redo,default));
        if(world.Raycast(CurrentEye(),Forward) is {} hit)
        {
            if(Raylib.IsMouseButtonPressed(MouseButton.Middle))palette.Assign(world.Get(hit.Hit));
            if(Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                if(PlayerControls.ShouldUseLever(world.Get(hit.Hit),sneakHeld))actions.Enqueue(new(ActionKind.Toggle,hit.Hit));
                else actions.Enqueue(new(ActionKind.Place,hit.Previous,palette.Selected));
            }
        }
    }
    bool CanRestore(Cell cell,Block block)=>block is Block.Air or Block.Wire or Block.Lever || !CollisionSystem.OverlapsPlayer(cell,feet,player.BodyHeight);
    void SimulationTick(float dt)
    {
        previousFeet=feet;
        player.Step(world,moveDirection,moveSpeed,jumpPending||jumpHeld,flyVertical,dt,sneakHeld);jumpPending=false;
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
        if(mineHeld&&world.Raycast(feet+new Vector3(0,player.EyeHeight,0),Forward) is {} hit)
        {
            if(mining!=hit.Hit){mining=hit.Hit;mineTime=0;}
            mineTime+=dt;
            if(mineTime>=MineDuration(world.Get(hit.Hit))){world.Mine(hit.Hit);mining=null;mineTime=0;}
        }
        else {mining=null;mineTime=0;}
        if(world.Circuits.PendingEventCount>0)world.Circuits.ProcessPending();
    }
}
