using System.Text.Json;

namespace BlockCircuit;

public sealed class RecipeBook
{
    sealed record Recipe(string Id,int Width,int Height,ContentId?[] Pattern,ContentId[] Ingredients,ItemStack Result,bool Shapeless);
    readonly ItemRegistry items;
    readonly List<Recipe> recipes=[];
    public int Count=>recipes.Count;
    RecipeBook(ItemRegistry items){this.items=items;}
    public static RecipeBook LoadDefault(ItemRegistry items)=>LoadJson(items,DefaultJson());
    public static string DefaultJson()
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Assets","recipes.json");
        if(File.Exists(path))return File.ReadAllText(path);
        using var stream=typeof(RecipeBook).Assembly.GetManifestResourceStream("BlockCircuit.recipes.json")
            ??throw new InvalidOperationException("Embedded recipes are missing.");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static RecipeBook LoadJson(ItemRegistry items,string json)
    {
        if(json.Length>1024*1024)throw new InvalidDataException("Recipe file exceeds 1 MiB.");
        var book=new RecipeBook(items);
        using var document=JsonDocument.Parse(json);
        if(document.RootElement.ValueKind!=JsonValueKind.Array||document.RootElement.GetArrayLength()>4096)throw new InvalidDataException("Expected a bounded recipe array.");
        var identifiers=new HashSet<string>(StringComparer.Ordinal);
        ContentId Known(string? name){
            var id=new ContentId(name!);
            if(!items.TryGetRuntimeId(id,out _))throw new InvalidDataException("Recipe references an unknown item: "+id);
            return id;
        }
        foreach(var entry in document.RootElement.EnumerateArray()){
            string id=new ContentId(entry.GetProperty("id").GetString()!).ToString();
            if(!identifiers.Add(id))throw new InvalidDataException("Duplicate recipe: "+id);
            var result=entry.GetProperty("result");var output=Known(result.GetProperty("item").GetString());
            int count=result.GetProperty("count").GetInt32();
            if(count<=0||count>items.Get(output).MaxStack)throw new InvalidDataException("Invalid recipe output count.");
            string? type=entry.GetProperty("type").GetString();
            if(type=="shapeless"){
                var inputs=entry.GetProperty("ingredients").EnumerateArray().Select(e=>Known(e.GetString())).ToArray();
                if(inputs.Length is <1 or >9)throw new InvalidDataException("Shapeless recipes need 1..9 inputs.");
                book.recipes.Add(new(id,0,0,[],inputs,new(output,count),true));
            }else if(type=="shaped"){
                string[] rows=entry.GetProperty("pattern").EnumerateArray().Select(e=>e.GetString()??"").ToArray();
                if(rows.Length is <1 or >3||rows[0].Length is <1 or >3||rows.Any(r=>r.Length!=rows[0].Length))throw new InvalidDataException("Invalid shaped dimensions.");
                var keys=entry.GetProperty("key");var symbols=new Dictionary<char,ContentId>();
                foreach(var key in keys.EnumerateObject()){
                    if(key.Name.Length!=1||key.Name[0]==' '||!symbols.TryAdd(key.Name[0],Known(key.Value.GetString())))throw new InvalidDataException("Invalid recipe key.");
                }
                var occupied=rows.SelectMany((r,y)=>r.Select((c,x)=>(c,x,y))).Where(v=>v.c!=' ').ToArray();
                if(occupied.Length==0)throw new InvalidDataException("Empty recipe.");
                int x0=occupied.Min(v=>v.x),y0=occupied.Min(v=>v.y),w=occupied.Max(v=>v.x)-x0+1,h=occupied.Max(v=>v.y)-y0+1;
                var pattern=new ContentId?[w*h];
                for(int y=0;y<h;y++)for(int x=0;x<w;x++){
                    char c=rows[y+y0][x+x0];if(c==' ')continue;
                    if(!symbols.TryGetValue(c,out var item))throw new InvalidDataException("Missing recipe key.");
                    pattern[y*w+x]=item;
                }
                book.recipes.Add(new(id,w,h,pattern,[],new(output,count),false));
            }else throw new InvalidDataException("Unsupported recipe type.");
        }
        return book;
    }
    public sealed record Match(string RecipeId,ItemStack Result,int[] Slots);
    public Match? Find(PlayerInventory bag,int size)
    {
        if(size is not(2 or 3))throw new ArgumentOutOfRangeException(nameof(size));
        var grid=size==2?bag.Crafting2:bag.Crafting3;
        foreach(var recipe in recipes){
            if(recipe.Shapeless){
                var occupied=grid.Select((s,i)=>(s,i)).Where(v=>v.s is not null).ToArray();
                if(occupied.Length!=recipe.Ingredients.Length)continue;
                var remaining=recipe.Ingredients.GroupBy(id=>id).ToDictionary(g=>g.Key,g=>g.Count());
                bool valid=true;
                foreach(var (stack,_) in occupied){
                    if(!remaining.TryGetValue(stack!.Id,out int count)||count==0){valid=false;break;}
                    remaining[stack.Id]=count-1;
                }
                if(valid)return new(recipe.Id,recipe.Result,occupied.Select(v=>v.i).ToArray());
            }else{
                for(int offsetY=0;offsetY<=size-recipe.Height;offsetY++)for(int offsetX=0;offsetX<=size-recipe.Width;offsetX++)for(int mirror=0;mirror<2;mirror++){
                    bool valid=true;var used=new List<int>();
                    for(int y=0;y<size&&valid;y++)for(int x=0;x<size;x++){
                        int px=x-offsetX,py=y-offsetY;
                        ContentId? expected=px>=0&&px<recipe.Width&&py>=0&&py<recipe.Height?recipe.Pattern[py*recipe.Width+(mirror==0?px:recipe.Width-1-px)]:null;
                        var stack=grid[y*size+x];
                        if(expected!=stack?.Id){valid=false;break;}
                        if(expected.HasValue)used.Add(y*size+x);
                    }
                    if(valid)return new(recipe.Id,recipe.Result,used.ToArray());
                }
            }
        }
        return null;
    }
    public int Craft(PlayerInventory bag,int size,bool toStorage=false,bool repeat=false,int maximumIterations=1024)
    {
        if(maximumIterations is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        int count=0;
        var candidate=PlayerInventory.Restore(items,bag.Capture());
        for(int iteration=0;iteration<(repeat?maximumIterations:1);iteration++){
            var match=Find(candidate,size);if(match is null)break;
            var next=PlayerInventory.Restore(items,candidate.Capture());
            var area=size==2?ItemArea.Craft2:ItemArea.Craft3;
            foreach(int slot in match.Slots)next.Take(new(area,slot),1);
            if(!next.PutCraftResult(match.Result,toStorage))break;
            candidate=next;count=checked(count+match.Result.Count);
        }
        if(count>0)bag.CommitCraft(candidate);
        return count;
    }
}
