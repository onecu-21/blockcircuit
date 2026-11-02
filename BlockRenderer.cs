using System.Numerics;
using System.Diagnostics;
using Raylib_cs;

namespace BlockCircuit;

/// <summary>A shared pixel atlas and persistent, revision-keyed chunk meshes.</summary>
public sealed class BlockRenderer : IDisposable
{
    const int Tile = 16, Columns = 32, AtlasSize = Tile * Columns;
    readonly Texture2D atlas;
    readonly Shader shader;
    Material material;
    readonly Dictionary<ChunkCoord, CachedChunk> cache = [];
    readonly List<ChunkCoord> removed = [];
    readonly List<CachedChunk> visible = [];
    readonly List<(CachedChunk Chunk, float Distance)> transparent = [];
    ChunkManager? source;
    float drawDistance = 64f;
    bool disposed;

    /// <summary>Visible range in blocks; changing it preserves all cached meshes.</summary>
    public float DrawDistance
    {
        get => drawDistance;
        set => drawDistance = float.IsFinite(value) ? Math.Clamp(value, 32f, 128f) : 64f;
    }

    public int CachedChunkCount => cache.Count;
    public int VisibleChunkCount { get; private set; }
    public int CulledChunkCount { get; private set; }
    public int VertexCount { get; private set; }
    public int TriangleCount => VertexCount / 3;
    public int MeshBuildsThisFrame { get; private set; }
    public long TotalMeshBuilds { get; private set; }
    public double LastBuildMilliseconds { get; private set; }
    public long GpuUploadCount { get; private set; }
    /// <summary>Cumulative allocations of one VAO and its three VBOs.</summary>
    public long GpuAllocationCount { get; private set; }
    public long ReleasedMeshCount { get; private set; }
    /// <summary>Number of live vertex buffers (three per allocated mesh).</summary>
    public int GpuBufferCount { get; private set; }

    sealed class CachedChunk(Chunk chunk)
    {
        public Chunk Source = chunk;
        public long Revision = long.MinValue;
        public readonly ChunkMesh Opaque = new();
        public readonly ChunkMesh Transparent = new();
        public Cell[] Circuits = [];
    }

    // Tile coordinates occupy R/G as raw bytes, and face lighting occupies B.
    // Repeating the pixel tile in the fragment shader keeps a merged quad's
    // texture at one tile per block, without sampling a neighboring atlas tile.
    const string VertexShader = """
        #version 330
        in vec3 vertexPosition;
        in vec2 vertexTexCoord;
        in vec4 vertexColor;
        uniform mat4 mvp;
        out vec2 voxelUV;
        flat out vec3 tileAndShade;
        void main()
        {
            voxelUV = vertexTexCoord;
            tileAndShade = vertexColor.rgb;
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }
        """;
    const string FragmentShader = """
        #version 330
        in vec2 voxelUV;
        flat in vec3 tileAndShade;
        uniform sampler2D texture0;
        out vec4 finalColor;
        void main()
        {
            vec2 tile = floor(tileAndShade.rg * 255.0 + 0.5);
            vec2 uv = (tile * 16.0 + 0.01 + fract(voxelUV) * 15.98) / 512.0;
            vec4 texel = texture(texture0, uv);
            if (texel.a < 0.01) discard;
            finalColor = vec4(texel.rgb * tileAndShade.b, texel.a);
        }
        """;

    public unsafe BlockRenderer()
    {
        var image = Raylib.GenImageColor(AtlasSize, AtlasSize, Color.Blank);
        try
        {
            var pixels = (Color*)image.Data;
            for (int id = 0; id < 256; id++)
            {
                var block = (Block)id;
                if (!BlockCatalog.IsValid(block)) continue;
                var info = BlockCatalog.Get(block);
                var name = block.ToString().ToLowerInvariant();
                for (int variant = 0; variant < 4; variant++)
                {
                    int tile = id * 4 + variant;
                    int left = tile % Columns * Tile, top = tile / Columns * Tile;
                    for (int y = 0; y < Tile; y++)
                    for (int x = 0; x < Tile; x++)
                        pixels[(top + y) * AtlasSize + left + x] = Pixel(info, name, variant, x, y);
                }
            }
            atlas = Raylib.LoadTextureFromImage(image);
            Raylib.SetTextureFilter(atlas, TextureFilter.Point);
            Raylib.SetTextureWrap(atlas, TextureWrap.Clamp);
        }
        finally { Raylib.UnloadImage(image); }

        try
        {
            shader = Raylib.LoadShaderFromMemory(VertexShader, FragmentShader);
            if (!Raylib.IsShaderReady(shader) || shader.Id == Rlgl.GetShaderIdDefault())
                throw new InvalidOperationException("Could not load the chunk atlas shader (OpenGL 3.3 required).");
            material = Raylib.LoadMaterialDefault();
            material.Shader = shader;
            Raylib.SetMaterialTexture(ref material, MaterialMapIndex.Albedo, atlas);
        }
        catch
        {
            if (shader.Id != 0 && shader.Id != Rlgl.GetShaderIdDefault()) Raylib.UnloadShader(shader);
            if (material.Maps != null) Raylib.MemFree(material.Maps);
            Raylib.UnloadTexture(atlas);
            throw;
        }
    }

    public void DrawWorld(World world, Camera3D camera, float aspect)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(source, world.Chunks))
        {
            ClearCache();
            source = world.Chunks;
        }

        removed.Clear();
        foreach (var coord in cache.Keys)
            if (!source.Chunks.ContainsKey(coord)) removed.Add(coord);
        foreach (var coord in removed)
        {
            Release(cache[coord]);
            cache.Remove(coord);
        }

        MeshBuildsThisFrame = 0;
        LastBuildMilliseconds = 0;
        visible.Clear();
        transparent.Clear();
        VisibleChunkCount = CulledChunkCount = VertexCount = GpuBufferCount = 0;
        var frustum = FrustumCuller.Create(camera.Position, camera.Target, camera.Up,
            camera.FovY * MathF.PI / 180f, aspect, .01f, 1000f);
        float drawDistanceSquared = drawDistance * drawDistance;

        foreach (var (coord, chunk) in source.Chunks)
        {
            if (!cache.TryGetValue(coord, out var cached))
            {
                cached = new CachedChunk(chunk);
                cache.Add(coord, cached);
            }
            if (!ReferenceEquals(cached.Source, chunk) || cached.Revision != chunk.Revision)
            {
                long start = Stopwatch.GetTimestamp();
                var data = ChunkMesher.Build(world, coord);
                Update(cached.Opaque, data.Opaque);
                Update(cached.Transparent, data.Transparent);
                cached.Circuits = data.Circuits;
                cached.Source = chunk;
                cached.Revision = chunk.Revision;
                LastBuildMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                MeshBuildsThisFrame++;
                TotalMeshBuilds++;
            }
            GpuBufferCount += cached.Opaque.BufferCount + cached.Transparent.BufferCount;
            var min = new Vector3(coord.X * 16, coord.Y * 16, coord.Z * 16);
            var max = min + new Vector3(16);
            // Keep a chunk if any part of its bounds is in range, including
            // when the camera is inside it or only its nearest edge is visible.
            var nearest = Vector3.Clamp(camera.Position, min, max);
            if (Vector3.DistanceSquared(camera.Position, nearest) > drawDistanceSquared ||
                !frustum.Intersects(min, max)) { CulledChunkCount++; continue; }
            VisibleChunkCount++;
            VertexCount += cached.Opaque.VertexCount + cached.Transparent.VertexCount;
            visible.Add(cached);
            if (cached.Transparent.VertexCount != 0)
                transparent.Add((cached, Vector3.DistanceSquared(camera.Position, (min + max) * .5f)));
        }

        // DrawMesh submits directly to GL. Flush any earlier immediate-mode
        // geometry first so its ordering and depth state stay intact.
        Rlgl.DrawRenderBatchActive();
        foreach (var cached in visible) cached.Opaque.Draw(material);
        Rlgl.SetTexture(0);
        foreach (var cached in visible)
        foreach (var cell in cached.Circuits)
            DrawCircuit(world, cell, world.Get(cell));

        if (transparent.Count > 0)
        {
            Rlgl.DrawRenderBatchActive();
            transparent.Sort(static (a,b) => b.Distance.CompareTo(a.Distance));
            Rlgl.DisableDepthMask();
            try
            {
                foreach (var item in transparent) item.Chunk.Transparent.Draw(material);
                Rlgl.DrawRenderBatchActive();
            }
            finally { Rlgl.EnableDepthMask(); }
        }
    }

    void Update(ChunkMesh mesh, CpuMeshData data)
    {
        long uploads = mesh.UploadCount, allocations = mesh.AllocationCount, released = mesh.ReleasedMeshCount;
        mesh.Update(data);
        GpuUploadCount += mesh.UploadCount - uploads;
        GpuAllocationCount += mesh.AllocationCount - allocations;
        ReleasedMeshCount += mesh.ReleasedMeshCount - released;
    }

    void Release(CachedChunk cached)
    {
        long before = cached.Opaque.ReleasedMeshCount + cached.Transparent.ReleasedMeshCount;
        cached.Opaque.Dispose();
        cached.Transparent.Dispose();
        ReleasedMeshCount += cached.Opaque.ReleasedMeshCount + cached.Transparent.ReleasedMeshCount - before;
    }

    void ClearCache()
    {
        foreach (var cached in cache.Values) Release(cached);
        cache.Clear();
        visible.Clear();
        transparent.Clear();
        GpuBufferCount = 0;
    }

    static void DrawCircuit(World world, Cell cell, Block block)
    {
        bool on = world.Power.GetValueOrDefault(cell)>0;
        var center = cell.Center;
        if (block == Block.Wire)
        {
            var color = on ? new Color(255,75,39,255) : new Color(113,32,32,255);
            center.Y = cell.Y+.025f;
            Raylib.DrawCube(center,.25f,.045f,.25f,color);
            foreach (var offset in World.Neighbors)
            {
                if (offset.Y!=0 || world.Get(cell+offset) is not (Block.Wire or Block.Lever or Block.Lamp)) continue;
                Raylib.DrawCube(center+new Vector3(offset.X*.30f,0,offset.Z*.30f),
                    offset.X!=0?.6f:.10f,.04f,offset.Z!=0?.6f:.10f,color);
            }
            if (on) Raylib.DrawCube(center+new Vector3(0,.025f,0),.09f,.008f,.09f,new Color(255,191,78,255));
        }
        else
        {
            var baseCenter = new Vector3(cell.X+.5f,cell.Y+.095f,cell.Z+.5f);
            Raylib.DrawCube(baseCenter,.5f,.19f,.5f,new Color(90,98,109,255));
            Raylib.DrawCubeWires(baseCenter,.502f,.192f,.502f,new Color(48,57,70,255));
            var end = new Vector3(cell.X+(on?.74f:.26f),cell.Y+.65f,cell.Z+.5f);
            Raylib.DrawCylinderEx(new(cell.X+.5f,cell.Y+.16f,cell.Z+.5f),end,.057f,.064f,7,new Color(160,116,68,255));
            Raylib.DrawCube(end,.14f,.16f,.14f,on?new Color(105,236,161,255):new Color(214,205,179,255));
        }
    }

    public void DrawIcon(Block block, int x, int y, int size)
    {
        if (block == Block.Air || size<=0) return;
        int tile = (int)block * 4;
        var source = new Rectangle(tile%Columns*Tile,tile/Columns*Tile,Tile,Tile);
        // A subtle offset gives each textured tile a readable physical edge at small sizes.
        Raylib.DrawRectangle(x+2,y+2,size,size,new Color(0,0,0,95));
        if (BlockCatalog.Get(block).Pattern == BlockPattern.Glass)
            Raylib.DrawRectangle(x,y,size,size,new Color(80,107,121,255));
        Raylib.DrawTexturePro(atlas,source,new Rectangle(x,y,size,size),Vector2.Zero,0,Color.White);
        Raylib.DrawRectangleLines(x,y,size,size,new Color(255,255,255,38));
    }

    static Color Rgb(uint color) => new((byte)(color>>16),(byte)(color>>8),(byte)color,(byte)255);
    static Color Shade(Color c,float value,byte alpha=255) => new(
        (byte)Math.Clamp((int)(c.R*value),0,255),
        (byte)Math.Clamp((int)(c.G*value),0,255),
        (byte)Math.Clamp((int)(c.B*value),0,255),alpha);
    static Color Mix(Color a,Color b,float amount) => new(
        (byte)(a.R+(b.R-a.R)*amount),(byte)(a.G+(b.G-a.G)*amount),
        (byte)(a.B+(b.B-a.B)*amount),(byte)(a.A+(b.A-a.A)*amount));
    static int Hash(int x,int y,int seed)
    {
        uint n = unchecked((uint)(x*374761393+y*668265263+seed*1442695041));
        n = (n^(n>>13))*1274126177u;
        return (int)((n^(n>>16))&0x7fffffff);
    }

    static Color Pixel(BlockInfo info,string name,int face,int x,int y)
    {
        var tint = Rgb(info.Tint);
        int hash = Hash(x,y,(int)info.Id*7+face);
        float noise = .91f+(hash%23)*.008f;
        var c = Shade(tint,noise);
        switch (info.Pattern)
        {
            case BlockPattern.Grass:
                if (face==1)
                {
                    c=Shade(tint,.8f+(hash%31)*.012f);
                    if ((x+y*3)%13==0) c=Mix(c,new Color(197,208,95,255),.35f);
                }
                else if (face==2 || y>2+Hash(x,0,9)%3)
                    c=Shade(new Color(132,91,57,255),.80f+(hash%30)*.014f);
                else c=Shade(tint,noise*.86f);
                break;
            case BlockPattern.Soil:
                c=Shade(tint,.77f+(hash%35)*.014f);
                if (hash%19==0) c=Shade(tint,.63f);
                break;
            case BlockPattern.Stone:
                c=Shade(tint,.84f+(hash%22)*.014f);
                if ((y+Hash(x/5,0,17)%3)%7==0 && x%5<4) c=Shade(c,.80f);
                if (name.Contains("smooth")) c=Shade(tint,.96f+(hash%7)*.012f);
                if (name.Contains("obsidian")) c=Mix(c,new Color(82,49,115,255),hash%6==0?.7f:.08f);
                break;
            case BlockPattern.Ore:
                var rock=name.Contains("deepslate")?new Color(70,76,86,255):new Color(118,125,132,255);
                if (name.Contains("quartz")) rock=new Color(107,58,55,255);
                c=Shade(rock,.82f+(hash%27)*.012f);
                int cluster=Hash(x/4,y/4,(int)info.Id);
                if (cluster%4!=0 && x%4>=cluster%2+1 && y%4>=1 && y%4<3)
                    c=Shade(tint,x%4==1||y%4==1?1.15f:.78f);
                break;
            case BlockPattern.Log:
                if (face is 1 or 2)
                {
                    int radius=Math.Max(Math.Abs(x-7),Math.Abs(y-7));
                    c=Mix(tint,new Color(216,180,116,255),.44f);
                    c=Shade(c,radius%3==0?.73f:noise*1.08f);
                    if (x is 0 or 15 || y is 0 or 15) c=Shade(tint,.70f);
                }
                else
                {
                    c=Shade(tint,(x%4==0?.64f:x%4==1?1.12f:.9f)*noise);
                    if (Hash(x/2,y/5,(int)info.Id)%7==0) c=Shade(c,.77f);
                }
                break;
            case BlockPattern.Planks:
                c=Shade(tint,noise*(y%4==0?1.12f:1f));
                if (y%4==3 || (x+(y/4%2)*8)%16==0) c=Shade(tint,.64f);
                else if (hash%11==0) c=Shade(c,.86f);
                break;
            case BlockPattern.Leaves:
                c=Shade(tint,.66f+Hash(x/3,y/3,(int)info.Id)%7*.09f);
                if (x%3==0 || y%3==0) c=Shade(c,.81f);
                if (hash%13==0) c=Shade(tint,1.24f);
                break;
            case BlockPattern.Bricks:
                bool mortar=y%5==0 || (x+((y/5)%2)*4)%8==0;
                c=mortar?Mix(tint,new Color(31,35,40,255),.58f):Shade(tint,noise*(y%5==1?1.13f:1f));
                break;
            case BlockPattern.Tiles:
                c=Shade(tint,noise*.98f);
                if (x%8==0 || y%8==0) c=Shade(tint,.59f);
                else if (x%8==1 || y%8==1) c=Shade(tint,1.16f);
                break;
            case BlockPattern.Glass:
                c=Mix(tint,Color.White,.15f);
                c.A=(byte)(x is 0 or 15 || y is 0 or 15 ? 215 : 30);
                if (name.Contains("honey") || name.Contains("slime"))
                {
                    c=Shade(tint,noise,135);
                    if (x is 0 or 15 || y is 0 or 15) c=Shade(tint,.80f,195);
                    if (name.Contains("honey") && y%5==0) c=Shade(tint,1.10f,155);
                    return c;
                }
                if (name.Contains("ice"))
                {
                    c=Shade(tint,noise,(byte)(name.Contains("packed")?155:80));
                    if ((x+y*2)%17==0) c=new Color(224,245,255,150);
                    return c;
                }
                if ((x-y==5 || x-y==6 || x-y==-6) && x>2 && x<13 && y>2 && y<13)
                    c=new Color(229,247,255,105);
                if (x==1 || y==1) c=Mix(c,new Color(245,255,255,150),.5f);
                return c;
            case BlockPattern.Wool:
                c=Shade(tint,((x+y)%2==0?.94f:1.03f)*(hash%7==0?.92f:1f));
                if ((x+y*2)%7==0) c=Shade(c,1.06f);
                break;
            case BlockPattern.Concrete:
                c=Shade(tint,.98f+(hash%6)*.007f);
                break;
            case BlockPattern.Terracotta:
                c=Shade(tint,.93f+(hash%13)*.01f);
                if (name.Contains("glazed"))
                {
                    int distance=Math.Abs(x-7)+Math.Abs(y-7);
                    if (distance%6<2) c=Mix(tint,new Color(243,224,173,255),.67f);
                    else if ((x/4+y/4)%2==0) c=Shade(c,.73f);
                }
                break;
            case BlockPattern.Metal:
                c=Shade(tint,.94f+y*.005f);
                if (x is 0 or 15 || y is 0 or 15) c=Shade(tint,.62f);
                else if (x==1 || y==1) c=Mix(tint,Color.White,.4f);
                else if (x==14 || y==14) c=Shade(tint,.8f);
                if ((x is 2 or 13)&&(y is 2 or 13)) c=Shade(tint,.66f);
                break;
            case BlockPattern.Lamp:
                var lamp=face==3?new Color(255,211,101,255):info.Id==Block.Lamp?Shade(tint,.44f):tint;
                c=Shade(lamp,.89f+(hash%12)*.012f);
                if (x is 0 or 15 || y is 0 or 15 || x is 7 or 8 || y is 7 or 8)
                    c=face==3?new Color(108,77,43,255):Shade(tint,.41f);
                else if (x%8 is 1 or 6 || y%8 is 1 or 6) c=Shade(lamp,face==3?1.15f:.83f);
                break;
            case BlockPattern.Wire:
                c=new Color(44,40,43,255);
                if ((x is >=6 and <=9)||(y is >=6 and <=9)) c=new Color(218,59,43,255);
                if ((x is 7 or 8)&&(y is 7 or 8)) c=new Color(255,168,84,255);
                break;
            case BlockPattern.Lever:
                c=new Color(75,86,98,255);
                if (x is >=3 and <=12 && y is >=10 and <=13) c=new Color(159,169,173,255);
                if (x>=5 && x<=8 && y>=3 && y<=10) c=new Color(193,146,92,255);
                if (x>=4 && x<=9 && y>=2 && y<=4) c=new Color(222,219,190,255);
                break;
        }

        // Distinctive utility/decor faces share the same catalog pattern foundation.
        if (name=="birchlog" && face==0)
        {
            c=Shade(tint,.93f+(hash%12)*.012f);
            if ((y+Hash(x/4,0,8)%3)%6==0 && x%4<3) c=new Color(64,61,55,255);
        }
        else if (name.Contains("mossy"))
        {
            if (Hash(x/3,y/3,11)%4==0 || hash%17==0) c=Shade(new Color(77,104,57,255),noise);
        }
        else if (name.Contains("cracked"))
        {
            if (x==(y/3*2+3)%16 || x==(y/3*2+4)%16) c=Shade(tint,.52f);
        }
        else if (name=="basalt" || name=="quartzpillar")
        {
            if (face==0) c=Shade(tint,x%4==0?.68f:x%4==1?1.15f:noise);
            else c=Shade(tint,Math.Max(Math.Abs(x-7),Math.Abs(y-7))%4==0?.76f:noise);
        }
        else if (name=="sponge")
        {
            c=Shade(tint,.94f+(hash%13)*.008f);
            if (Hash(x/3,y/3,7)%3==0 && x%3!=0 && y%3!=0) c=Shade(tint,.58f);
        }
        else if (name=="magmablock")
        {
            c=new Color(77,45,37,255);
            if ((x+Hash(0,y/4,33)%3)%5==0 || (y+Hash(x/4,0,21)%3)%5==0)
                c=Shade(new Color(247,126,41,255),noise);
        }
        else if (name=="glowstone")
        {
            c=Shade(tint,Hash(x/3,y/3,18)%4==0?.62f:1.12f);
            if (x%3==0 || y%3==0) c=Shade(c,.8f);
            if (hash%11==0) c=new Color(255,238,173,255);
        }
        else if (name=="sealantern")
        {
            c=Mix(tint,new Color(235,252,238,255),.7f);
            if (x is 0 or 15 || y is 0 or 15) c=new Color(74,129,126,255);
            else if (x is 1 or 14 || y is 1 or 14) c=new Color(127,177,164,255);
            else c=Shade(c,.97f+hash%5*.01f);
        }
        else if (name=="honeycombblock")
        {
            if ((x+(y/4%2)*3)%6==0 || y%4==0) c=Shade(tint,.58f);
            else c=Shade(tint,noise*1.08f);
        }
        else if (name=="amethystblock")
        {
            c=Shade(tint,.70f+Hash(x/4,y/4,26)%7*.10f);
            if ((x+y)%7==0) c=Mix(c,new Color(235,188,255,255),.6f);
        }
        else if (name.Contains("bookshelf") && face==0)
        {
            if (y is 0 or 7 or 8 or 15) c=Shade(tint,.68f);
            else
            {
                Color[] books=[new(164,60,51,255),new(69,115,146,255),new(190,157,70,255),new(75,133,91,255)];
                c=books[(x/3+y/8)%books.Length];
                if (x%3==0) c=Shade(c,.58f);
                if (y is 2 or 11) c=Mix(c,new Color(242,219,153,255),.65f);
            }
        }
        else if (name.Contains("craft") || name.Contains("workbench"))
        {
            if (face==1 && (x%5==0 || y%5==0)) c=new Color(69,44,30,255);
            if (face==0 && (x is 2 or 13 || y==4)) c=Shade(tint,.5f);
        }
        else if (name.Contains("tnt") && face==0)
        {
            c=y is >=5 and <=10?new Color(220,210,183,255):new Color(177,54,43,255);
            if (y<5 || y>10) c=Shade(c,x%4==0?.65f:1f);
            else if ((y==6 && x is >=2 and <=13)||(x is 3 or 7 or 12 && y<10)) c=new Color(43,40,39,255);
        }
        else if (name.Contains("cactus"))
        {
            if (face==0 && x%4==0) c=Shade(tint,.6f);
            if ((x+y*5)%23==0) c=new Color(219,221,161,255);
        }
        else if (name.Contains("melon") && face==0) c=Shade(tint,x%5<2?.63f:noise);
        else if (name.Contains("pumpkin"))
        {
            if (face==1 && x is >=6 and <=9 && y is >=6 and <=9) c=new Color(84,89,43,255);
            else if (face==0) c=Shade(tint,x%4==0?.72f:noise);
        }
        else if (name.Contains("hay"))
        {
            c=Shade(tint,(x%3==0?.8f:1f)*noise);
            if (face==0 && y is 3 or 4 or 11 or 12) c=new Color(139,75,43,255);
        }
        else if (name.Contains("furnace") && face==0)
        {
            if (x is >=3 and <=12 && y is >=7 and <=12) c=new Color(33,38,43,255);
            if (x is >=3 and <=12 && y is 3 or 4) c=new Color(64,69,77,255);
        }
        if (info.Pattern is not (BlockPattern.Metal or BlockPattern.Lamp or BlockPattern.Wire or BlockPattern.Lever))
        {
            if (x==0 || y==0) c=Shade(c,1.025f);
            if (x==15 || y==15) c=Shade(c,.91f);
        }
        return c;
    }

    public unsafe void Dispose()
    {
        if (disposed) return;
        ClearCache();
        // UnloadMaterial also owns its texture/shader in native raylib. The
        // atlas is shared with UI icons, so free only the maps here and release
        // each shared GPU resource exactly once below.
        if (material.Maps != null) Raylib.MemFree(material.Maps);
        material = default;
        Raylib.UnloadShader(shader);
        Raylib.UnloadTexture(atlas);
        disposed=true;
    }
}
