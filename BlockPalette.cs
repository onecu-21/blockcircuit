using System.Numerics;
using Raylib_cs;
using BlockCircuit;

namespace BlockCircuit
{
    public sealed class BlockPaletteModel
    {
        public Block[] Hotbar { get; } = [Block.Grass, Block.Dirt, Block.Stone, Block.Coal, Block.Iron, Block.Wire, Block.Lever, Block.Lamp];
        public int SelectedSlot { get; set; }
        public BlockCategory? Category { get; set; }
        public string Query { get; set; } = "";
        public Block Selected => Hotbar[SelectedSlot];
        public BlockInfo[] Filtered() => BlockCatalog.Placeable.Where(b =>
            (Category is null || b.Category == Category) && b.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        public bool Assign(Block block)
        {
            if (!BlockCatalog.IsValid(block) || block is Block.Air or Block.Bedrock) return false;
            Hotbar[SelectedSlot] = block;
            return true;
        }
    }
}

sealed partial class Game
{
    readonly BlockPaletteModel palette = new();
    bool inventoryOpen, searchFocused;
    int palettePage;
    readonly Color muted = new(153,174,186,255), tileColor = new(34,46,58,255);
    Rectangle PaletteRect => new((Raylib.GetScreenWidth()-Math.Min(1120,Raylib.GetScreenWidth()-40))/2,
        (Raylib.GetScreenHeight()-Math.Min(700,Raylib.GetScreenHeight()-40))/2,
        Math.Min(1120,Raylib.GetScreenWidth()-40),Math.Min(700,Raylib.GetScreenHeight()-40));
    void ToggleInventory()
    {
        inventoryOpen=!inventoryOpen;searchFocused=false;mining=null;mineTime=0;
        if(inventoryOpen)Raylib.EnableCursor();else Raylib.DisableCursor();
    }
    void DrawTextFit(string text,int x,int y,int size,int maxWidth,Color color)
    {
        while(size>10&&Raylib.MeasureText(text,size)>maxWidth)size--;
        Raylib.DrawText(text,x,y,size,color);
    }
    bool Button(Rectangle r,string text,bool active=false)
    {
        bool hover=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),r);
        Raylib.DrawRectangleRec(r,active?accent:hover?new Color(51,68,81,255):tileColor);
        DrawTextFit(text,(int)r.X+10,(int)r.Y+10,15,(int)r.Width-18,active?ink:Color.White);
        return hover&&Raylib.IsMouseButtonPressed(MouseButton.Left);
    }
    void DrawPalette()
    {
        var r=PaletteRect;int x=(int)r.X,y=(int)r.Y,w=(int)r.Width,h=(int)r.Height;
        Raylib.DrawRectangle(0,0,Raylib.GetScreenWidth(),Raylib.GetScreenHeight(),new Color(9,16,24,200));
        Raylib.DrawRectangleRec(r,ink);
        Raylib.DrawRectangle(x,y,4,h,accent);
        Raylib.DrawText("BLOCK LIBRARY",x+24,y+21,28,Color.White);
        Raylib.DrawText($"{BlockCatalog.Placeable.Count} blocks  /  choose a block for your hotbar",x+25,y+57,15,muted);
        if(Button(new(x+w-99,y+20,76,35),"CLOSE")){ToggleInventory();return;}
        int gridX=x+180,gridY=y+120,gridW=w-202;
        var search=new Rectangle(gridX,y+79,gridW,32);
        if(Raylib.IsMouseButtonPressed(MouseButton.Left))searchFocused=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),search);
        Raylib.DrawRectangleRec(search,tileColor);
        if(searchFocused)Raylib.DrawRectangleLinesEx(search,1,accent);
        Raylib.DrawText(palette.Query.Length==0?(searchFocused?"Type a block name...":"Click to search (English names)"):palette.Query+(searchFocused?"_":""),gridX+11,y+88,15,palette.Query.Length>0?Color.White:muted);
        if(searchFocused)
        {
            int c;while((c=Raylib.GetCharPressed())!=0)if(c>=32&&c<127&&palette.Query.Length<32){palette.Query+=(char)c;palettePage=0;}
            if(Raylib.IsKeyPressed(KeyboardKey.Backspace)&&palette.Query.Length>0){palette.Query=palette.Query[..^1];palettePage=0;}
            if(Raylib.IsKeyPressed(KeyboardKey.Enter))searchFocused=false;
        }
        else for(int i=0;i<8;i++)if(Raylib.IsKeyPressed((KeyboardKey)((int)KeyboardKey.One+i)))palette.SelectedSlot=i;
        if(Button(new(x+23,y+79,140,32),"All blocks",palette.Category==null)){palette.Category=null;palettePage=0;}
        int categoryY=gridY;
        foreach(var category in Enum.GetValues<BlockCategory>())
        {
            if(Button(new(x+23,categoryY,140,30),category.ToString(),palette.Category==category)){palette.Category=category;palettePage=0;}
            categoryY+=35;
        }
        int columns=Math.Max(4,gridW/110),rows=Math.Max(2,(h-238)/86),pageSize=columns*rows;
        var items=palette.Filtered();int pages=Math.Max(1,(items.Length+pageSize-1)/pageSize);
        palettePage=Math.Clamp(palettePage,0,pages-1);
        float wheel=Raylib.GetMouseWheelMove();
        if(wheel!=0){palettePage=Math.Clamp(palettePage+(wheel<0?1:-1),0,pages-1);searchFocused=false;}
        if(!searchFocused&&Raylib.IsKeyPressed(KeyboardKey.PageDown))palettePage=Math.Min(pages-1,palettePage+1);
        if(!searchFocused&&Raylib.IsKeyPressed(KeyboardKey.PageUp))palettePage=Math.Max(0,palettePage-1);
        BlockInfo? hovered=null;int cellW=gridW/columns;
        for(int i=0;i<pageSize&&palettePage*pageSize+i<items.Length;i++)
        {
            var b=items[palettePage*pageSize+i];int bx=gridX+(i%columns)*cellW,by=gridY+(i/columns)*86;
            var card=new Rectangle(bx,by,cellW-6,80);bool hover=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),card);
            Raylib.DrawRectangleRec(card,hover?new Color(47,65,76,255):tileColor);
            if(palette.Selected==b.Id)Raylib.DrawRectangleLinesEx(card,2,accent);
            renderer.DrawIcon(b.Id,bx+10,by+8,32);
            Raylib.DrawText(world.Inventory[(int)b.Id].ToString(),bx+cellW-40,by+12,12,muted);
            string[] words=b.Name.Split(' ');string first=words.Length>1?string.Join(' ',words[..^1]):b.Name;
            string second=words.Length>1?words[^1]:"";
            DrawTextFit(first,bx+8,by+46,13,cellW-19,Color.White);
            DrawTextFit(second,bx+8,by+62,12,cellW-19,muted);
            if(hover)
            {
                hovered=b;
                if(Raylib.IsMouseButtonPressed(MouseButton.Left)){palette.Assign(b.Id);Notice($"{b.Name} selected in slot {palette.SelectedSlot+1}.");}
            }
        }
        if(items.Length==0)Raylib.DrawText("No matching blocks. Try another name or category.",gridX+10,gridY+25,17,muted);
        int footer=y+h-105;
        if(Button(new(gridX,footer,70,30),"< PREV"))palettePage=Math.Max(0,palettePage-1);
        if(Button(new(gridX+gridW-70,footer,70,30),"NEXT >"))palettePage=Math.Min(pages-1,palettePage+1);
        string pageText=$"{palettePage+1} / {pages}   |   {items.Length} blocks   |   scroll to browse";
        DrawTextFit(pageText,gridX+90,footer+9,14,gridW-180,muted);
        string detail=hovered is {} info?$"{info.Name}  /  {info.Category}  /  click to assign":$"Select slot 1-8, then click any block.   TAB / ESC to return.";
        DrawTextFit(detail,gridX,footer+40,14,gridW,accent);
        for(int i=0;i<8;i++)
        {
            int bx=gridX+i*55,by=y+h-39;var slot=new Rectangle(bx,by,49,30);
            Raylib.DrawRectangleRec(slot,palette.SelectedSlot==i?accent:tileColor);
            renderer.DrawIcon(palette.Hotbar[i],bx+23,by+5,20);
            Raylib.DrawText((i+1).ToString(),bx+8,by+8,13,palette.SelectedSlot==i?ink:Color.White);
            if(Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),slot)&&Raylib.IsMouseButtonPressed(MouseButton.Left)){palette.SelectedSlot=i;searchFocused=false;}
        }
    }
}
