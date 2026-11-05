using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    string menuTextFocus="";
    double lastMenuClick;
    Guid? lastClickedWorld;
    int titleChoice;
    readonly Color menuGray=new(102,105,110,255),menuLight=new(195,197,202,255),menuDark=new(38,40,45,255);
    unsafe void MenuTextField(string id,Rectangle box,MenuText input,int maximum,string placeholder="")
    {
        bool hover=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),box);
        bool active=menuTextFocus==id;
        if(hover&&Raylib.IsMouseButtonPressed(MouseButton.Left)){menuTextFocus=id;input.End();active=true;}
        Raylib.DrawRectangleRec(box,new Color(12,13,16,240));
        Raylib.DrawRectangleLinesEx(box,active?2:1,active?Color.White:menuGray);
        if(active){
            bool ctrl=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
            if(ctrl&&Raylib.IsKeyPressed(KeyboardKey.A))input.SelectAll();
            if(ctrl&&Raylib.IsKeyPressed(KeyboardKey.C)&&input.SelectedAll)Raylib.SetClipboardText(input.Text);
            if(ctrl&&Raylib.IsKeyPressed(KeyboardKey.V))input.Insert(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)Raylib.GetClipboardText())??"",maximum);
            if(Raylib.IsKeyPressed(KeyboardKey.Left))input.Move(-1);
            if(Raylib.IsKeyPressed(KeyboardKey.Right))input.Move(1);
            if(Raylib.IsKeyPressed(KeyboardKey.Home))input.Home();
            if(Raylib.IsKeyPressed(KeyboardKey.End))input.End();
            if(Raylib.IsKeyPressed(KeyboardKey.Backspace)||Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace))input.Backspace();
            if(Raylib.IsKeyPressed(KeyboardKey.Delete)||Raylib.IsKeyPressedRepeat(KeyboardKey.Delete))input.Delete();
            int key;while((key=Raylib.GetCharPressed())!=0)if(!ctrl&&key>=32&&key<=0x10ffff&&!(key is >=0xd800 and <=0xdfff))input.Insert(char.ConvertFromUtf32(key),maximum);
        }
        Raylib.BeginScissorMode((int)box.X+6,(int)box.Y+3,(int)box.Width-12,(int)box.Height-6);
        string shown=input.Text;int size=18;
        int cursorWidth=Raylib.MeasureText(shown[..input.Cursor],size);
        int offset=active?Math.Max(0,cursorWidth-(int)box.Width+24):0;
        int tx=(int)box.X+10-offset,ty=(int)box.Y+((int)box.Height-size)/2;
        if(active&&input.SelectedAll)Raylib.DrawRectangle(tx,ty-2,Raylib.MeasureText(shown,size),size+4,new Color(45,75,145,255));
        Raylib.DrawText(shown.Length==0?placeholder:shown,tx,ty,size,shown.Length==0?new Color(140,142,148,255):Color.White);
        if(active&&(int)(Raylib.GetTime()*2)%2==0)Raylib.DrawRectangle(tx+cursorWidth,ty,2,size,Color.White);
        Raylib.EndScissorMode();
    }
    bool MenuButton(Rectangle box,string label,bool enabled=true,bool keyboard=false)
    {
        bool hovered=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),box);
        Raylib.DrawRectangleRec(box,enabled?(hovered||keyboard?new Color(126,133,151,255):menuGray):new Color(67,69,74,255));
        Raylib.DrawRectangle((int)box.X,(int)box.Y,(int)box.Width,2,enabled?menuLight:menuGray);
        Raylib.DrawRectangle((int)box.X,(int)box.Y,2,(int)box.Height,enabled?menuLight:menuGray);
        Raylib.DrawRectangle((int)box.X,(int)(box.Y+box.Height-2),(int)box.Width,2,menuDark);
        Raylib.DrawRectangle((int)(box.X+box.Width-2),(int)box.Y,2,(int)box.Height,menuDark);
        if(enabled&&(hovered||keyboard))Raylib.DrawRectangleLinesEx(box,1,Color.White);
        int size=18;while(size>12&&Raylib.MeasureText(label,size)>box.Width-16)size--;
        int x=(int)(box.X+(box.Width-Raylib.MeasureText(label,size))/2),y=(int)(box.Y+(box.Height-size)/2);
        Raylib.DrawText(label,x+1,y+2,size,new Color(20,21,25,255));Raylib.DrawText(label,x,y,size,enabled?Color.White:new Color(145,146,150,255));
        return enabled&&((hovered&&Raylib.IsMouseButtonPressed(MouseButton.Left))||(keyboard&&(Raylib.IsKeyPressed(KeyboardKey.Enter)||Raylib.IsKeyPressed(KeyboardKey.Space))));
    }
    void MenuCentered(string text,int y,int size,Color color)
    {
        Raylib.DrawText(text,(Raylib.GetScreenWidth()-Raylib.MeasureText(text,size))/2,y,size,color);
    }
    void MenuHeading(string title,string subtitle="")
    {
        MenuCentered(title,40,30,Color.White);
        if(subtitle.Length>0)MenuCentered(subtitle,82,15,new Color(205,209,217,255));
    }
}
