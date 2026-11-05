using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    bool libraryView;
    int craftingSize=2;
    Cell? craftingBench;
    RecipeBook recipeBook=null!;
    readonly InventoryGesture inventoryGesture=new();
    void SyncHotbar()
    {
        if(world.Items is not {} bag)return;
        for(int i=0;i<9;i++)palette.Hotbar[i]=bag.Slots[i] is {} stack&&world.Registries.Blocks.TryGetBlock(stack.Id,out var block)?block:Block.Air;
    }
    void PickBlock(Block block)
    {
        if(world.Items is null){palette.Assign(block);return;}
        if(block is Block.Air or Block.Bedrock||!world.Items.AssignHotbar(world.Registries.Blocks.GetId(block),palette.SelectedSlot))Notice("No owned stack or free space for this block.");
        if(NetworkClientActive&&block is not (Block.Air or Block.Bedrock))SubmitClient(new(NetCommandKind.Assign){Item=world.Registries.Blocks.GetId(block).ToString(),Slot=palette.SelectedSlot});
        SyncHotbar();
    }
    void OpenCrafting(Cell bench)
    {
        craftingBench=bench;craftingSize=3;libraryView=false;inventoryOpen=true;searchFocused=false;
        inventoryGesture.Cancel();ResetSimulation();Raylib.EnableCursor();
    }
    bool BenchAvailable()=>craftingSize==2||craftingBench is {} cell&&world.Get(cell)==Block.CraftingTable&&Vector3.DistanceSquared(cell.Center,feet)<49;
    string ItemName(ItemStack? stack)=>stack is null?"Empty":world.Registries.Items.TryGetRuntimeId(stack.Id,out int id)?world.Registries.Items.Get(id).Name:stack.Id.ToString();
    void DrawItem(ItemStack? stack,int x,int y,int size=32)
    {
        if(stack is null)return;
        if(world.Registries.Blocks.TryGetBlock(stack.Id,out var block))renderer.DrawIcon(block,x,y,size,world.Registries.Blocks);
        else{
            Raylib.DrawRectangle(x+size/3,y+3,Math.Max(4,size/5),size-6,new Color(187,143,85,255));
            if(!world.Registries.Items.TryGetRuntimeId(stack.Id,out _))Raylib.DrawText("?",x+8,y+6,20,accent);
        }
    }
    void DrawPalette()
    {
        if(world.Items is null||libraryView){DrawLibrary();return;}
        DrawInventoryPanel();
    }
    Vector3 DropPosition()=>new(Math.Clamp(feet.X+Forward.X*1.2f,-World.MaximumPlayerCoordinate,World.MaximumPlayerCoordinate),
        Math.Clamp(feet.Y+.5f,0,255),Math.Clamp(feet.Z+Forward.Z*1.2f,-World.MaximumPlayerCoordinate,World.MaximumPlayerCoordinate));
    void DrawInventoryPanel()
    {
        var bag=world.Items!;var r=PaletteRect;int x=(int)r.X,y=(int)r.Y,w=(int)r.Width,h=(int)r.Height;
        Raylib.DrawRectangle(0,0,Raylib.GetScreenWidth(),Raylib.GetScreenHeight(),new Color(9,16,24,210));
        Raylib.DrawRectangleRec(r,ink);Raylib.DrawRectangle(x,y,4,h,accent);
        Raylib.DrawText("INVENTORY",x+24,y+20,28,Color.White);
        Raylib.DrawText("36 slots / 9 hotbar",x+25,y+55,14,muted);
        if(Button(new(x+w-231,y+20,125,35),"LIBRARY")){libraryView=true;inventoryGesture.Cancel();return;}
        if(Button(new(x+w-99,y+20,76,35),"CLOSE")){ToggleInventory();return;}
        int cell=50,gridX=x+25,craftY=y+106;
        Raylib.DrawText(craftingSize==3?"CRAFTING TABLE  3 x 3":"PERSONAL CRAFTING  2 x 2",gridX,y+83,14,accent);
        ItemSlot? hovered=null;ItemStack? hoverStack=null;
        void Slot(ItemSlot slot,int sx,int sy){
            var box=new Rectangle(sx,sy,46,46);bool over=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),box);
            Raylib.DrawRectangleRec(box,over?new Color(56,74,84,255):tileColor);
            var stack=bag.Get(slot);DrawItem(stack,sx+8,sy+4);
            if(stack is not null)Raylib.DrawText(stack.Count.ToString(),sx+27,sy+32,12,Color.White);
            if(slot.Area==ItemArea.Storage&&slot.Index<9){
                Raylib.DrawText((slot.Index+1).ToString(),sx+3,sy+3,10,muted);
                if(slot.Index==palette.SelectedSlot)Raylib.DrawRectangleLinesEx(box,2,accent);
            }
            if(over){hovered=slot;hoverStack=stack;Raylib.DrawRectangleLinesEx(box,1,accent);}
        }
        var area=craftingSize==2?ItemArea.Craft2:ItemArea.Craft3;
        for(int i=0;i<craftingSize*craftingSize;i++)Slot(new(area,i),gridX+i%craftingSize*cell,craftY+i/craftingSize*cell);
        var match=BenchAvailable()?recipeBook.Find(bag,craftingSize):null;
        var resultRect=new Rectangle(gridX+205,craftY+38,64,64);
        Raylib.DrawText(">",gridX+170,craftY+55,25,accent);
        Raylib.DrawRectangleRec(resultRect,tileColor);
        DrawItem(match?.Result,(int)resultRect.X+16,(int)resultRect.Y+7);
        if(match is not null)Raylib.DrawText(match.Result.Count.ToString(),(int)resultRect.X+44,(int)resultRect.Y+46,14,accent);
        bool resultHover=Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(),resultRect);
        bool shift=Raylib.IsKeyDown(KeyboardKey.LeftShift)||Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool inventoryInputReady=!NetworkClientActive||multiplayerClient!.Ready;
        if(inventoryInputReady&&resultHover&&Raylib.IsMouseButtonPressed(MouseButton.Left)&&BenchAvailable()){
            int made=recipeBook.Craft(bag,craftingSize,shift,shift,maximumIterations:NetworkClientActive?64:1024);
            SubmitClient(new(NetCommandKind.Craft){Size=craftingSize,ToStorage=shift,Repeat=shift,Bench=craftingBench});
            if(made==0)Notice("No matching recipe or room for the result.");
        }
        int instructions=x+535;
        DrawTextFit("Left: pick up / merge / swap",instructions,y+109,14,w-555,Color.White);
        DrawTextFit("Right: split / place one",instructions,y+135,14,w-555,Color.White);
        DrawTextFit("Drag: spread   Shift+click: transfer",instructions,y+161,14,w-555,Color.White);
        DrawTextFit("Shift+result: craft all that fit",instructions,y+187,14,w-555,Color.White);
        DrawTextFit("Q: drop one   Ctrl+Q: drop stack",instructions,y+213,14,w-555,accent);
        DrawTextFit($"Legacy reserve: {bag.Reserve.Count} item types (LIBRARY)",instructions,y+239,13,w-555,muted);
        Raylib.DrawText("BACKPACK",gridX,y+252,14,muted);
        for(int i=9;i<36;i++)Slot(new(ItemArea.Storage,i),gridX+(i-9)%9*cell,y+275+(i-9)/9*cell);
        Raylib.DrawText("HOTBAR",gridX,y+435,14,muted);
        for(int i=0;i<9;i++)Slot(new(ItemArea.Storage,i),gridX+i*cell,y+457);
        if(inventoryInputReady&&hovered is {} target){
            if(Raylib.IsKeyPressed(KeyboardKey.Q)){
                bool all=Raylib.IsKeyDown(KeyboardKey.LeftControl)||Raylib.IsKeyDown(KeyboardKey.RightControl);
                if(!SubmitClient(new(NetCommandKind.Drop){Source=bag.Cursor is null?target:null,Whole=all}))world.DropItem(bag.Cursor is null?target:null,all?int.MaxValue:1,DropPosition());
            }
            foreach(var button in new[]{MouseButton.Left,MouseButton.Right})if(Raylib.IsMouseButtonPressed(button)){
                inventoryGesture.Begin(bag,target,button==MouseButton.Right,shift);
            }
            if(inventoryGesture.Active&&Raylib.IsMouseButtonDown(inventoryGesture.RightButton?MouseButton.Right:MouseButton.Left))inventoryGesture.Hover(bag,target);
        }
        if(inventoryGesture.Active&&Raylib.IsMouseButtonReleased(inventoryGesture.RightButton?MouseButton.Right:MouseButton.Left))inventoryGesture.Release(bag);
        if(bag.Cursor is {} carried){
            var mouse=Raylib.GetMousePosition();DrawItem(carried,(int)mouse.X+10,(int)mouse.Y+8);
            Raylib.DrawText(carried.Count.ToString(),(int)mouse.X+34,(int)mouse.Y+36,14,accent);
        }
        DrawTextFit(hoverStack is not null?ItemName(hoverStack):"E / ESC closes. Crafting inputs and carried items are saved.",x+25,y+h-44,14,w-50,muted);
        SyncHotbar();
    }
    void DrawDroppedItems()
    {
        foreach(var drop in world.DroppedItems){
            if(Vector3.DistanceSquared(drop.Position,feet)>settings.DrawDistance*settings.DrawDistance)continue;
            var tint=world.Registries.Blocks.TryGetBlock(new(drop.Item),out var block)?world.Registries.Blocks.GetInfo(block).Tint:0xC99C62u;
            Raylib.DrawCube(drop.Position,.25f,.25f,.25f,new Color((byte)(tint>>16),(byte)(tint>>8),(byte)tint,(byte)255));
            Raylib.DrawCubeWires(drop.Position,.26f,.26f,.26f,accent);
        }
    }
}
