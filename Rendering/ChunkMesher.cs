using System.Numerics;

namespace BlockCircuit;

/// <summary>
/// Builds a chunk only when its contents or an adjacent boundary changes. Geometry is
/// independent of the graphics API, so visibility and greedy merging can be tested headlessly.
/// </summary>
public static class ChunkMesher
{
    const int AtlasColumns = 32;
    static readonly Cell[] FaceOffsets = [new(1,0,0),new(-1,0,0),new(0,1,0),new(0,-1,0),new(0,0,1),new(0,0,-1)];
    static readonly float[] FaceLight = [.79f, .87f, 1f, .53f, .91f, .71f];

    // Same winding and texture orientation as the original immediate-mode renderer.
    static readonly Vector3[][] FaceVertices =
    [
        [new(1,0,1), new(1,0,0), new(1,1,0), new(1,1,1)],
        [new(0,0,0), new(0,0,1), new(0,1,1), new(0,1,0)],
        [new(0,1,1), new(1,1,1), new(1,1,0), new(0,1,0)],
        [new(0,0,0), new(1,0,0), new(1,0,1), new(0,0,1)],
        [new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)],
        [new(1,0,0), new(0,0,0), new(0,1,0), new(1,1,0)]
    ];

    // Keeping every material input in the key prevents merging visually distinct faces,
    // even if later lighting or transparency rules differ for the same block ID.
    readonly record struct FaceKey(Block Block, Block TextureBlock, byte Variant, byte Direction, byte Light,
        bool Translucent, bool CatalogTransparent, bool Powered);

    /// <summary>Capture must run on the world's owner thread; workers never read live collections.</summary>
    public static ChunkMeshSnapshot Capture(World world, ChunkCoord coord)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!coord.IsValid) throw new ArgumentOutOfRangeException(nameof(coord));
        if (!world.Registries.Blocks.IsFrozen) throw new InvalidOperationException("Mesh metadata must be frozen.");
        const int size = ChunkCoord.Size;
        const int haloSize = size + 2;
        var origin = coord.Origin;
        var halo = new Block[haloSize * haloSize * haloSize];
        var powered = new bool[size * size * size];
        var circuits = new List<Cell>();
        for (int y = -1; y <= size; y++)
        for (int z = -1; z <= size; z++)
        for (int x = -1; x <= size; x++)
        {
            bool valid = origin.TryOffset(new Cell(x, y, z), out var cell);
            var block = valid ? world.Get(cell) : Block.Air;
            halo[((y + 1) * haloSize + z + 1) * haloSize + x + 1] = block;
            if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size) continue;
            if (block is Block.Wire or Block.Lever) circuits.Add(cell);
            powered[(y * size + z) * size + x] = block == Block.Lamp && world.Power.GetValueOrDefault(cell) > 0;
        }
        return new(coord, world.Registries.Blocks, halo, powered, circuits.ToArray());
    }

    public static ChunkMeshData Build(World world, ChunkCoord coord, bool greedy = true) => Build(Capture(world, coord), greedy);

    public static ChunkMeshData Build(ChunkMeshSnapshot snapshot, bool greedy = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        const int size = ChunkCoord.Size;
        var origin = snapshot.Coord.Origin;
        var opaque = new MeshBuilder();
        var transparent = new MeshBuilder();
        var mask = new FaceKey[size * size];
        int exposedFaces = 0;

        for (int face = 0; face < 6; face++)
        for (int slice = 0; slice < size; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(mask);
            var offset = FaceOffsets[face];
            for (int v = 0; v < size; v++)
            for (int u = 0; u < size; u++)
            {
                var local = LocalCell(face, slice, u, v);
                var block = snapshot.GetHalo(local.X, local.Y, local.Z);
                if (block is Block.Air or Block.Wire or Block.Lever) continue;
                var neighbor = snapshot.GetHalo(local.X + offset.X, local.Y + offset.Y, local.Z + offset.Z);
                if (!FaceVisible(snapshot.Blocks, block, neighbor)) continue;

                var info = snapshot.Blocks.GetInfo(block);
                bool powered = block == Block.Lamp && snapshot.IsPowered(local);
                byte variant = (byte)(powered ? 3 : face == 2 ? 1 : face == 3 ? 2 : 0);
                bool emissive = powered || block is Block.Glowstone or Block.SeaLantern or Block.MagmaBlock;
                byte light = (byte)(255 * (emissive ? .95f : FaceLight[face]));
                mask[v * size + u] = new(block, info.TextureBlock, variant, (byte)face, light,
                    info.Pattern == BlockPattern.Glass, info.Transparent, powered);
                exposedFaces++;
            }

            for (int v = 0; v < size; v++)
            for (int u = 0; u < size;)
            {
                var key = mask[v * size + u];
                if (key.Block == Block.Air) { u++; continue; }
                int width = 1, height = 1;
                if (greedy)
                {
                    while (u + width < size && mask[v * size + u + width] == key) width++;
                    while (v + height < size && MatchingRow(mask, size, u, v + height, width, key)) height++;
                }

                var cell = origin + LocalCell(face, slice, u, v);
                var scale = face < 2 ? new Vector3(1, height, width)
                    : face < 4 ? new Vector3(width, 1, height) : new Vector3(width, height, 1);
                var builder = key.Translucent ? transparent : opaque;
                builder.AddQuad(cell, scale, width, height, key);
                for (int dv = 0; dv < height; dv++)
                    Array.Clear(mask, (v + dv) * size + u, width);
                u += width;
            }
        }

        return new(opaque.Finish(), transparent.Finish(), snapshot.CircuitCells.ToArray(), exposedFaces);

    }

    static Cell LocalCell(int face, int slice, int u, int v) => face < 2
        ? new(slice, v, u) : face < 4 ? new(u, slice, v) : new(u, v, slice);

    static bool MatchingRow(FaceKey[] mask, int size, int u, int v, int width, FaceKey key)
    {
        for (int i = 0; i < width; i++)
            if (mask[v * size + u + i] != key) return false;
        return true;
    }

    static bool FaceVisible(BlockRegistry blocks, Block block, Block neighbor)
    {
        if (neighbor is Block.Air or Block.Wire or Block.Lever) return true;
        var info = blocks.GetInfo(neighbor);
        if (!info.Transparent && info.Pattern != BlockPattern.Glass) return false;
        return block != neighbor;
    }

    sealed class MeshBuilder
    {
        readonly List<float> positions = [];
        readonly List<float> texCoords = [];
        readonly List<byte> colors = [];

        public void AddQuad(Cell cell, Vector3 scale, int width, int height, FaceKey key)
        {
            var corners = FaceVertices[key.Direction];
            var origin = new Vector3(cell.X, cell.Y, cell.Z);
            int tile = (int)key.TextureBlock * 4 + key.Variant;
            byte column = (byte)(tile % AtlasColumns), row = (byte)(tile / AtlasColumns);
            // Fractional UV sampling in the shader repeats the atlas tile across the
            // merged rectangle. Each voxel still occupies exactly one tile period.
            Vertex(0, 0, height);
            Vertex(1, width, height);
            Vertex(2, width, 0);
            Vertex(0, 0, height);
            Vertex(2, width, 0);
            Vertex(3, 0, 0);

            void Vertex(int corner, float u, float v)
            {
                var point = origin + corners[corner] * scale;
                positions.Add(point.X);
                positions.Add(point.Y);
                positions.Add(point.Z);
                texCoords.Add(u);
                texCoords.Add(v);
                colors.Add(column);
                colors.Add(row);
                colors.Add(key.Light);
                colors.Add(255);
            }
        }

        public CpuMeshData Finish() => positions.Count == 0 ? CpuMeshData.Empty
            : new(positions.ToArray(), texCoords.ToArray(), colors.ToArray());
    }
}

/// <summary>Privately owned halo and lighting values with immutable Registry metadata.</summary>
public sealed class ChunkMeshSnapshot
{
    readonly Block[] halo;
    readonly bool[] powered;
    readonly Cell[] circuits;
    public ChunkCoord Coord { get; }
    public BlockRegistry Blocks { get; }
    internal ReadOnlySpan<Cell> CircuitCells => circuits;
    internal ChunkMeshSnapshot(ChunkCoord coord, BlockRegistry blocks, Block[] halo, bool[] powered, Cell[] circuits)
    { Coord = coord; Blocks = blocks; this.halo = halo; this.powered = powered; this.circuits = circuits; }
    internal Block GetHalo(int x, int y, int z) => halo[((y + 1) * 18 + z + 1) * 18 + x + 1];
    internal bool IsPowered(Cell local) => powered[(local.Y * 16 + local.Z) * 16 + local.X];
}
