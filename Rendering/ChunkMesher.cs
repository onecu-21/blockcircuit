using System.Numerics;

namespace BlockCircuit;

/// <summary>
/// Builds a chunk only when its contents or an adjacent boundary changes. Geometry is
/// independent of the graphics API, so visibility and greedy merging can be tested headlessly.
/// </summary>
public static class ChunkMesher
{
    const int AtlasColumns = 32;
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
    readonly record struct FaceKey(Block Block, byte Variant, byte Direction, byte Light,
        bool Translucent, bool CatalogTransparent, bool Powered);

    public static ChunkMeshData Build(World world, ChunkCoord coord, bool greedy = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        const int size = ChunkCoord.Size;
        const int haloSize = size + 2;
        var origin = coord.Origin;
        var halo = new Block[haloSize * haloSize * haloSize];
        var circuits = new List<Cell>();
        var opaque = new MeshBuilder();
        var transparent = new MeshBuilder();
        var mask = new FaceKey[size * size];
        int exposedFaces = 0;

        // Snapshot a one-voxel halo as well as the chunk itself. Faces at chunk borders
        // consult their real neighbors, including cells in a separately cached chunk.
        for (int y = -1; y <= size; y++)
        for (int z = -1; z <= size; z++)
        for (int x = -1; x <= size; x++)
        {
            var cell = origin + new Cell(x, y, z);
            var block = world.Get(cell);
            halo[HaloIndex(x, y, z)] = block;
            if (x >= 0 && x < size && y >= 0 && y < size && z >= 0 && z < size &&
                block is Block.Wire or Block.Lever)
                circuits.Add(cell);
        }

        for (int face = 0; face < 6; face++)
        for (int slice = 0; slice < size; slice++)
        {
            Array.Clear(mask);
            var offset = World.Neighbors[face];
            for (int v = 0; v < size; v++)
            for (int u = 0; u < size; u++)
            {
                var local = LocalCell(face, slice, u, v);
                var block = halo[HaloIndex(local.X, local.Y, local.Z)];
                if (block is Block.Air or Block.Wire or Block.Lever) continue;
                var neighbor = halo[HaloIndex(local.X + offset.X, local.Y + offset.Y, local.Z + offset.Z)];
                if (!FaceVisible(block, neighbor)) continue;

                var info = BlockCatalog.Get(block);
                bool powered = block == Block.Lamp && world.Power.GetValueOrDefault(origin + local) > 0;
                byte variant = (byte)(powered ? 3 : face == 2 ? 1 : face == 3 ? 2 : 0);
                bool emissive = powered || block is Block.Glowstone or Block.SeaLantern or Block.MagmaBlock;
                byte light = (byte)(255 * (emissive ? .95f : FaceLight[face]));
                mask[v * size + u] = new(block, variant, (byte)face, light,
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

        return new(opaque.Finish(), transparent.Finish(), circuits.ToArray(), exposedFaces);

        static int HaloIndex(int x, int y, int z) => ((y + 1) * haloSize + z + 1) * haloSize + x + 1;
    }

    static Cell LocalCell(int face, int slice, int u, int v) => face < 2
        ? new(slice, v, u) : face < 4 ? new(u, slice, v) : new(u, v, slice);

    static bool MatchingRow(FaceKey[] mask, int size, int u, int v, int width, FaceKey key)
    {
        for (int i = 0; i < width; i++)
            if (mask[v * size + u + i] != key) return false;
        return true;
    }

    static bool FaceVisible(Block block, Block neighbor)
    {
        if (neighbor is Block.Air or Block.Wire or Block.Lever) return true;
        var info = BlockCatalog.Get(neighbor);
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
            int tile = (int)key.Block * 4 + key.Variant;
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
