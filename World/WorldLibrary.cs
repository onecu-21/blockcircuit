using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

public sealed record WorldEntry(Guid Id,string Name,int? Seed,bool Classic,DateTime CreatedUtc,DateTime LastPlayedUtc,bool Legacy,string? Error)
{
    public bool CanPlay { get; init; }
}

/// <summary>Disk catalog for explicit title-screen selection. Listing never creates or loads a World.</summary>
public sealed class WorldLibrary
{
    const int MetadataVersion=1,MaximumMetadataBytes=16*1024,MaximumEntries=4096;
    static readonly Guid LegacyId=Guid.Parse("00000000-0000-0000-0000-000000000001");
    static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,MaxDepth=12};
    readonly GameRegistries registries;
    readonly string dataRoot,worldsRoot,trashRoot;
    readonly StringComparison comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    internal sealed record Info(int Version,Guid Id,string Name,int? Seed,bool Classic,DateTime CreatedUtc,DateTime LastPlayedUtc,bool Legacy);
    public string DataRoot=>dataRoot;
    public WorldLibrary(string dataRoot,GameRegistries? registries=null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        this.dataRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        this.registries=registries??GameRegistries.Default;
        worldsRoot=Path.Combine(this.dataRoot,"worlds");trashRoot=Path.Combine(this.dataRoot,"trash");
    }
    public IReadOnlyList<WorldEntry> List()
    {
        EnsureNoLinks(dataRoot);var entries=new List<WorldEntry>();
        if(Exists(LegacyId))entries.Add(ReadEntry(LegacyId));
        if(Directory.Exists(worldsRoot)){
            EnsureNoLinks(worldsRoot);
            foreach(string directory in Directory.EnumerateDirectories(worldsRoot).Take(MaximumEntries)){
                string name=Path.GetFileName(directory);
                if(!Guid.TryParseExact(name,"N",out var id)||id==Guid.Empty||id==LegacyId)continue;
                entries.Add(ReadEntry(id));
            }
        }
        return entries.OrderByDescending(e=>e.LastPlayedUtc).ThenByDescending(e=>e.CreatedUtc).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public string GetSavePath(WorldEntry entry){ArgumentNullException.ThrowIfNull(entry);return GetSavePath(entry.Id);}
    public string GetSavePath(Guid id)
    {
        RequireId(id);
        string path=id==LegacyId?Path.Combine(dataRoot,"world.json"):Path.Combine(WorldDirectory(id),"world.json");
        EnsureNoLinks(path);EnsureNoLinks(path+".bak");EnsureNoLinks(path+".chunks");EnsureNoLinks(path+".lock");return path;
    }
    public (WorldEntry Entry,World World) Create(string name,int seed,bool classic=false,IEnumerable<Block>? preferredHotbar=null)
    {
        name=ValidName(name);EnsureNoLinks(dataRoot);EnsureNoLinks(worldsRoot);
        Directory.CreateDirectory(worldsRoot);
        Guid id;string directory;
        do{id=Guid.NewGuid();directory=WorldDirectory(id);}while(id==LegacyId||Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        // No other world's files are touched. A failed create remains recoverable in its own folder.
        var world=classic?new World(seed:seed,legacyTerrain:true,registries:registries):World.CreateStreaming(seed,registries);
        world.EnableStreaming();world.EnableSlotInventory(preferredHotbar);
        world.Save(GetSavePath(id),world.Spawn(),MathF.PI,-.18f);
        var now=DateTime.UtcNow;var info=new Info(MetadataVersion,id,name,seed,classic,now,now,false);
        WriteInfo(info);
        return (Entry(info,null,true),world);
    }
    public (World World,Vector3 Position,float Yaw,float Pitch) Open(WorldEntry entry,IEnumerable<Block>? preferredHotbar=null)
    {
        ArgumentNullException.ThrowIfNull(entry);return Open(entry.Id,preferredHotbar);
    }
    public (World World,Vector3 Position,float Yaw,float Pitch) Open(Guid id,IEnumerable<Block>? preferredHotbar=null)
    {
        var entry=RequireEntry(id);
        if(!entry.CanPlay)throw new InvalidDataException(entry.Error??"This world has no readable save.");
        string path=GetSavePath(id);
        var candidate=new World(false,registries:registries);
        var pose=candidate.Load(path);
        candidate.EnableStreaming();candidate.EnableSlotInventory(preferredHotbar);
        var info=InfoFor(entry) with{Seed=candidate.Seed,Classic=candidate.TerrainVersion==0,LastPlayedUtc=DateTime.UtcNow};
        WriteInfo(info);
        return (candidate,pose.Position,pose.Yaw,pose.Pitch);
    }
    public WorldEntry Rename(Guid id,string name)
    {
        name=ValidName(name);
        var entry=RequireEntry(id);var info=InfoFor(entry) with{Name=name};
        WriteInfo(info);return Entry(info,null,entry.CanPlay);
    }
    public WorldEntry MarkPlayed(Guid id)
    {
        var entry=RequireEntry(id);var info=InfoFor(entry) with{LastPlayedUtc=DateTime.UtcNow};
        WriteInfo(info);return Entry(info,entry.Error,entry.CanPlay);
    }
    /// <summary>Explicit portable backup, with no original save or last-played mutation.</summary>
    public string Backup(Guid id)
    {
        var entry=RequireEntry(id);
        if(!entry.CanPlay)throw new InvalidDataException(entry.Error??"This world has no readable save.");
        string source=GetSavePath(id);
        var candidate=new World(false,registries:registries);
        var pose=candidate.Load(source);
        string backups=Path.Combine(dataRoot,"backups");EnsureNoLinks(backups);Directory.CreateDirectory(backups);
        string target=Path.Combine(backups,$"world-{id:N}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        EnsureNoLinks(target);
        candidate.ExportSnapshot(target,pose.Position,pose.Yaw,pose.Pitch);
        return target;
    }
    /// <summary>Recoverable deletion. Legacy artifacts are moved individually, never the data root.</summary>
    public string Trash(Guid id)
    {
        var entry=RequireEntry(id);
        string path=GetSavePath(id);EnsureNoLinks(trashRoot);Directory.CreateDirectory(trashRoot);
        string destination=Path.Combine(trashRoot,$"{id:N}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        EnsureNoLinks(destination);
        if(id!=LegacyId){
            string directory=WorldDirectory(id);EnsureNoLinks(directory);
            // Windows will not rename the parent directory while this handle is open,
            // even with delete sharing. Probe the save lock, then release it before
            // moving; a concurrent Windows writer still makes Directory.Move fail.
            using(new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){}
            Directory.Move(directory,destination);return destination;
        }
        // A synthetic legacy catalog entry may not have an info directory yet.
        var info=InfoFor(entry);
        Directory.CreateDirectory(destination);
        using var legacyWriter=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Delete);
        var moved=new List<(string Source,string Target,bool Directory)>();
        try{
            foreach(string source in new[]{path,path+".bak",path+".chunks",path+".lock"}){
                EnsureNoLinks(source);
                bool isDirectory=Directory.Exists(source);
                if(!isDirectory&&!File.Exists(source))continue;
                string target=Path.Combine(destination,Path.GetFileName(source));
                if(isDirectory)Directory.Move(source,target);else File.Move(source,target);
                moved.Add((source,target,isDirectory));
            }
            string metadata=WorldDirectory(id);
            if(Directory.Exists(metadata)){
                EnsureNoLinks(metadata);string target=Path.Combine(destination,"catalog");
                Directory.Move(metadata,target);moved.Add((metadata,target,true));
            }
            AtomicSave.Write(Path.Combine(destination,"world-info.json"),JsonSerializer.Serialize(info,JsonOptions));
            return destination;
        }catch{
            for(int i=moved.Count-1;i>=0;i--){
                var move=moved[i];
                if(move.Directory){if(Directory.Exists(move.Target))Directory.Move(move.Target,move.Source);}
                else if(File.Exists(move.Target))File.Move(move.Target,move.Source);
            }
            throw;
        }
    }
    WorldEntry RequireEntry(Guid id)
    {
        RequireId(id);if(!Exists(id))throw new KeyNotFoundException("This world is no longer in the library.");
        return ReadEntry(id);
    }
    bool Exists(Guid id)
    {
        if(id==LegacyId)return File.Exists(Path.Combine(dataRoot,"world.json"))||File.Exists(Path.Combine(dataRoot,"world.json.bak"))||Directory.Exists(WorldDirectory(id));
        return Directory.Exists(WorldDirectory(id));
    }
    WorldEntry ReadEntry(Guid id)
    {
        string metadata=Path.Combine(WorldDirectory(id),"world-info.json");
        try{
            EnsureNoLinks(WorldDirectory(id));
            Info? info=null;string? warning=null;
            try{if(File.Exists(metadata))info=ReadInfo(metadata,id);}catch(Exception e)when(IsCatalogError(e)){warning="World details could not be read.";}
            if(info is null&&File.Exists(metadata+".bak")){
                try{info=ReadInfo(metadata+".bak",id);warning="World details recovered from backup.";}catch(Exception e)when(IsCatalogError(e)){warning="World details could not be read.";}
            }
            string save=GetSavePath(id);
            bool available=File.Exists(save)||File.Exists(save+".bak");
            if(info is null){
                DateTime modified=SafeTime(File.Exists(save)?save:File.Exists(save+".bak")?save+".bak":WorldDirectory(id));
                int? seed=null;bool classic=false;
                if(id==LegacyId){(seed,classic)=LegacyHint(File.Exists(save)?save:save+".bak");}
                info=new(MetadataVersion,id,id==LegacyId?"Imported World":"Unnamed World",seed,classic,modified,modified,id==LegacyId);
                if(id!=LegacyId)warning??="World details are missing.";
            }
            if(!available)warning="The world save is missing.";
            return Entry(info,warning,available);
        }catch(Exception e)when(IsCatalogError(e)){
            return new(id,id==LegacyId?"Imported World":"Unreadable World",null,false,DateTime.UnixEpoch,DateTime.UnixEpoch,id==LegacyId,"This world folder cannot be accessed safely."){CanPlay=false};
        }
    }
    static WorldEntry Entry(Info info,string? error,bool playable)=>new(info.Id,info.Name,info.Seed,info.Classic,info.CreatedUtc,info.LastPlayedUtc,info.Legacy,error){CanPlay=playable};
    static Info InfoFor(WorldEntry entry)=>new(MetadataVersion,entry.Id,entry.Name,entry.Seed,entry.Classic,entry.CreatedUtc,entry.LastPlayedUtc,entry.Legacy);
    Info ReadInfo(string path,Guid expected)
    {
        EnsureNoLinks(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        if(stream.Length is <2 or >MaximumMetadataBytes)throw new InvalidDataException("World details exceed their size limit.");
        byte[] bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
        var info=JsonSerializer.Deserialize<Info>(bytes,JsonOptions)??throw new InvalidDataException("Empty world details.");
        if(info.Version!=MetadataVersion||info.Id!=expected||info.Legacy!=(expected==LegacyId)||
            info.Name!=ValidName(info.Name)||info.CreatedUtc<DateTime.UnixEpoch||info.LastPlayedUtc<DateTime.UnixEpoch||
            info.CreatedUtc>DateTime.UtcNow.AddDays(1)||info.LastPlayedUtc>DateTime.UtcNow.AddDays(1))
            throw new InvalidDataException("Invalid world details.");
        return info;
    }
    void WriteInfo(Info info)
    {
        string directory=WorldDirectory(info.Id);EnsureNoLinks(directory);Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"world-info.json");
        EnsureNoLinks(path);EnsureNoLinks(path+".bak");
        bool validPrevious=false;
        if(File.Exists(path))try{_=ReadInfo(path,info.Id);validPrevious=true;}catch(Exception e)when(IsCatalogError(e)){}
        AtomicSave.Write(path,JsonSerializer.Serialize(info,JsonOptions),validPrevious?path+".bak":null);
    }
    static string ValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);name=name.Trim();
        if(name.Length is <1 or >64||name.Any(char.IsControl))throw new ArgumentException("Use a world name from 1 to 64 characters without control characters.");
        return name;
    }
    string WorldDirectory(Guid id){RequireId(id);return Path.Combine(worldsRoot,id.ToString("N"));}
    static void RequireId(Guid id){if(id==Guid.Empty)throw new ArgumentException("Missing world identity.",nameof(id));}
    void EnsureNoLinks(string path)
    {
        path=Path.GetFullPath(path);
        if(!path.Equals(dataRoot,comparison)&&!path.StartsWith(dataRoot+Path.DirectorySeparatorChar,comparison))throw new ArgumentException("World path escapes the library.");
        string? current=path;
        while(current is not null){
            if(File.Exists(current)||Directory.Exists(current)){
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked world paths are not supported.");
            }
            if(current.Equals(dataRoot,comparison))break;
            current=Path.GetDirectoryName(current);
        }
    }
    static DateTime SafeTime(string path)
    {
        try{var value=File.GetLastWriteTimeUtc(path);return value<DateTime.UnixEpoch?DateTime.UnixEpoch:value;}catch(IOException){return DateTime.UnixEpoch;}
    }
    static (int? Seed,bool Classic) LegacyHint(string path)
    {
        // Never materialize a 64 MiB legacy save just to draw a row in the world list.
        try{
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            byte[] prefix=new byte[Math.Min(64*1024,checked((int)Math.Min(stream.Length,64*1024)))];
            int length=stream.Read(prefix);
            var reader=new Utf8JsonReader(prefix.AsSpan(0,length),isFinalBlock:stream.Position==stream.Length,state:default);
            string? property=null;int? seed=null;bool classic=false;
            while(reader.Read()){
                if(reader.TokenType==JsonTokenType.PropertyName&&reader.CurrentDepth==1)property=reader.GetString();
                else if(reader.TokenType==JsonTokenType.Number&&reader.CurrentDepth==1){
                    if(property=="Seed"&&reader.TryGetInt32(out int value))seed=value;
                    if(property=="TerrainVersion"&&reader.TryGetInt32(out value))classic=value==0;
                    property=null;
                }
            }
            return(seed,classic);
        }catch(Exception e)when(e is IOException or JsonException or ArgumentException){return(null,false);}
    }
    static bool IsCatalogError(Exception e)=>e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException;
}
