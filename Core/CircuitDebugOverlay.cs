using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    void DrawCircuitLines(Vector3 eye)
    {
        foreach(var cell in world.Circuits.Cells)
        {
            if(Vector3.DistanceSquared(eye,cell.Center)>20*20)continue;
            int power=world.Power.GetValueOrDefault(cell);
            var color=power>0?new Color(70+power*12,250-power*6,84,255):new Color(91,150,194,210);
            Raylib.DrawCubeWires(cell.Center,1.025f,1.025f,1.025f,color);
            foreach(var d in World.Neighbors)
            {
                if(d.X+d.Y+d.Z<0)continue;
                var other=cell+d;
                if(world.Circuits.GetNetworkId(other)!=null)Raylib.DrawLine3D(cell.Center,other.Center,color);
            }
        }
    }
    void DrawCircuitPanel((Cell Hit,Cell Previous)? target)
    {
        int height=Raylib.GetScreenHeight(),width=Raylib.GetScreenWidth();
        int y=debugOverlay?365:175;
        Raylib.DrawRectangle(20,y,345,151,ink);
        Raylib.DrawText("CIRCUITS  /  F4",34,y+12,17,accent);
        string targetText=target is {} t&&world.Circuits.GetNetworkId(t.Hit) is {} network?
            $"Network {network} | signal {world.Power.GetValueOrDefault(t.Hit)} / 15":"Aim at a wire, lever, or lamp.";
        string[] lines=
        [
            $"{world.Circuits.NetworkCount} networks | {world.Circuits.CircuitCellCount} cells",
            $"Last update {world.Circuits.LastUpdatedNetworks} networks / {world.Circuits.LastVisitedCells} cells",
            $"Pending events {world.Circuits.PendingEventCount}",targetText,
            $"Undo {world.UndoCount}  /  Redo {world.RedoCount}"
        ];
        for(int i=0;i<lines.Length;i++)Raylib.DrawText(lines[i],34,y+39+i*19,13,Color.White);
        var camera=new Camera3D{Position=CurrentEye(),Target=CurrentEye()+Forward,Up=Vector3.UnitY,FovY=settings.FieldOfView,Projection=CameraProjection.Perspective};
        int labels=0;
        foreach(var cell in world.Circuits.Cells)
        {
            var point=cell.Center+new Vector3(0,.55f,0);var delta=point-camera.Position;
            if(delta.LengthSquared()>12*12||Vector3.Dot(delta,Forward)<=0||labels>=64)continue;
            var screen=Raylib.GetWorldToScreen(point,camera);
            if(screen.X<10||screen.X>width-30||screen.Y<10||screen.Y>height-100)continue;
            string text=world.Power.GetValueOrDefault(cell).ToString();
            Raylib.DrawRectangle((int)screen.X-8,(int)screen.Y-5,26,23,ink);Raylib.DrawText(text,(int)screen.X-3,(int)screen.Y,13,accent);labels++;
        }
    }
}
