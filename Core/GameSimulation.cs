using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    enum ActionKind { Place, Toggle, Undo, Redo, Drop }
    readonly record struct PlayerAction(ActionKind Kind,Cell Cell,Block Block=Block.Air,int Slot=-1,bool Whole=false);
    readonly Queue<PlayerAction> actions=[];
    readonly PlayerControls controls=new();
    readonly SimulationInput simulationInput=new();
    Vector3 moveDirection;float moveSpeed=PhysicsProfile.WalkSpeed,flyVertical;bool jumpHeld,mineHeld,sneakHeld;
    Vector3 CurrentEye()=>Vector3.Lerp(previousFeet,feet,clock.Alpha)+new Vector3(0,player.EyeHeight,0);
    void ResetSimulation()
    {
        clock.ResetAccumulator();previousFeet=feet;actions.Clear();jumpHeld=mineHeld=sneakHeld=false;simulationInput.Reset();controls.Reset();
        mining=null;mineTime=0;
    }
    void CaptureInput()
    {
        var mouse=Raylib.GetMouseDelta();yaw-=mouse.X*settings.MouseSensitivity;
        pitch=Math.Clamp(pitch-mouse.Y*settings.MouseSensitivity,-1.5f,1.5f);
        if(NetworkClientActive&&!multiplayerClient!.Ready){actions.Clear();simulationInput.Reset();mineHeld=false;return;}
        bool jumpPressed=Raylib.IsKeyPressed(KeyboardKey.Space);
        jumpHeld=Raylib.IsKeyDown(KeyboardKey.Space);
        sneakHeld=Raylib.IsKeyDown(KeyboardKey.LeftShift);
        bool forwardHeld=Raylib.IsKeyDown(KeyboardKey.W)&&!Raylib.IsKeyDown(KeyboardKey.S);
        if(controls.Sample(Raylib.GetTime(),jumpPressed,Raylib.IsKeyPressed(KeyboardKey.W),forwardHeld,
            Raylib.IsKeyDown(KeyboardKey.LeftControl),sneakHeld))
        {
            simulationInput.QueueFlightToggle();jumpPressed=false;
            Notice((player.Flying^simulationInput.FlightTogglePending)?"Flight ON: SPACE up, SHIFT down. Double SPACE to land.":"Flight OFF: gravity enabled.");
        }
        var forward=new Vector3(MathF.Sin(yaw),0,MathF.Cos(yaw));var right=new Vector3(-forward.Z,0,forward.X);
        moveDirection=Vector3.Zero;
        if(Raylib.IsKeyDown(KeyboardKey.W))moveDirection+=forward;if(Raylib.IsKeyDown(KeyboardKey.S))moveDirection-=forward;
        if(Raylib.IsKeyDown(KeyboardKey.D))moveDirection+=right;if(Raylib.IsKeyDown(KeyboardKey.A))moveDirection-=right;
        moveSpeed=(sneakHeld||player.Crouching)&&!(player.Flying^simulationInput.FlightTogglePending)?PhysicsProfile.SneakSpeed:controls.Sprinting?PhysicsProfile.SprintSpeed:PhysicsProfile.WalkSpeed;
        bool control=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
        flyVertical=(jumpHeld?1:0)-(sneakHeld?1:0);
        simulationInput.Sample(moveDirection,moveSpeed,jumpPressed,jumpHeld,flyVertical,sneakHeld);
        mineHeld=Raylib.IsMouseButtonDown(MouseButton.Left);
        SyncHotbar();
        for(int i=0;i<palette.Hotbar.Length;i++)if(Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One+i)))palette.SelectedSlot=i;
        palette.SelectedSlot=PlayerControls.ScrollSlot(palette.SelectedSlot,Raylib.GetMouseWheelMove(),palette.Hotbar.Length);
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Z))actions.Enqueue(new(ActionKind.Undo,default));
        if(control&&Raylib.IsKeyPressed(KeyboardKey.Y))actions.Enqueue(new(ActionKind.Redo,default));
        if(Raylib.IsKeyPressed(KeyboardKey.Q))actions.Enqueue(new(ActionKind.Drop,default,Slot:palette.SelectedSlot,Whole:control));
        if(world.Raycast(CurrentEye(),Forward) is {} hit)
        {
            if(Raylib.IsMouseButtonPressed(MouseButton.Middle))PickBlock(world.Get(hit.Hit));
            if(Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                if(world.Items is not null&&world.Get(hit.Hit)==Block.CraftingTable&&!sneakHeld){OpenCrafting(hit.Hit);return;}
                if(PlayerControls.ShouldUseLever(world.Get(hit.Hit),sneakHeld))actions.Enqueue(new(ActionKind.Toggle,hit.Hit));
                else actions.Enqueue(new(ActionKind.Place,hit.Previous,palette.Selected,palette.SelectedSlot));
            }
        }
    }
    bool CanRestore(Cell cell,Block block)=>block is Block.Air or Block.Wire or Block.Lever || !CollisionSystem.OverlapsPlayer(cell,feet,player.BodyHeight);
    void SimulationTick(float dt)
    {
        if(!NetworkClientActive)world.Tick();
        previousFeet=feet;
        var input=simulationInput.Consume();
        if(NetworkClientActive&&!multiplayerClient!.Ready){actions.Clear();player.VerticalVelocity=0;mining=null;mineTime=0;SyncHotbar();return;}
        if(input.ToggleFlight)player.Flying=!player.Flying;
        player.Step(world,input.Direction,input.Speed,input.Jump&&!input.ToggleFlight,input.FlyVertical,dt,input.Sneak);
        if(Vector3.DistanceSquared(previousFeet,feet)>4)previousFeet=feet;
        while(actions.TryDequeue(out var action))
        {
            if(NetworkClientActive){
                switch(action.Kind){
                    case ActionKind.Place:SubmitClient(new(NetCommandKind.Place){Cell=action.Cell,Slot=action.Slot});break;
                    case ActionKind.Toggle:SubmitClient(new(NetCommandKind.Toggle){Cell=action.Cell});break;
                    case ActionKind.Drop:SubmitClient(new(NetCommandKind.Drop){Source=new(ItemArea.Storage,action.Slot),Whole=action.Whole});break;
                    default:Notice("Undo/redo is available in singleplayer.");break;
                }
                continue;
            }
            if(multiplayerHost is not null&&action.Kind is ActionKind.Undo or ActionKind.Redo){Notice("Undo/redo is available in singleplayer.");continue;}
            switch(action.Kind)
            {
                case ActionKind.Place:
                    if(!CanRestore(action.Cell,action.Block)||(action.Block is not (Block.Wire or Block.Lever)&&multiplayerHost?.AnyPlayerOverlaps(action.Cell)==true))Notice("Cannot place a block inside the player.");
                    else if(!(world.Items is null?world.Place(action.Cell,action.Block):
                        (uint)action.Slot<9&&palette.Hotbar[action.Slot]==action.Block&&world.PlaceFromSlot(action.Cell,action.Slot)))Notice("Need inventory, empty space, and support for wire/lever.");
                    break;
                case ActionKind.Drop:world.DropItem(new(ItemArea.Storage,action.Slot),action.Whole?int.MaxValue:1,DropPosition());break;
                case ActionKind.Toggle:world.Toggle(action.Cell);break;
                case ActionKind.Undo:Notice(world.Undo(CanRestore)?"Edit undone.":"Nothing to undo, or the player blocks restoration.");break;
                case ActionKind.Redo:Notice(world.Redo(CanRestore)?"Edit redone.":"Nothing to redo, or the player blocks restoration.");break;
            }
        }
        if(mineHeld&&world.Raycast(feet+new Vector3(0,player.EyeHeight,0),Forward) is {} hit)
        {
            if(mining!=hit.Hit){mining=hit.Hit;mineTime=0;}
            mineTime+=dt;
            if(mineTime>=MineDuration(world.Get(hit.Hit))){if(!SubmitClient(new(NetCommandKind.Mine){Cell=hit.Hit}))world.Mine(hit.Hit);mining=null;mineTime=0;}
        }
        else {mining=null;mineTime=0;}
        if(!NetworkClientActive)world.CollectDrops(feet);
        SyncHotbar();
        if(!NetworkClientActive&&world.Circuits.PendingEventCount>0)world.Circuits.ProcessPending();
    }
}
