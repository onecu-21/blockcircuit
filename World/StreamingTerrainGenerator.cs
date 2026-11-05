namespace BlockCircuit;

/// <summary>Coordinate-addressable terrain for the streamed world. Each worker owns its noise and output.</summary>
public static class StreamingTerrainGenerator
{
    public const int Version = 2;
    public const int MinimumY = 0;
    public const int MaximumY = 63;
    const int TreeRadius = 2;
    const int TreeGrid = 6;
    readonly record struct Tree(int X, int Z, int Ground, int Height, Block Log, Block Leaves, double Priority);

    public static Block[] Generate(int seed, ChunkCoord coordinate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!coordinate.IsValid) throw new ArgumentOutOfRangeException(nameof(coordinate));
        var result = new Block[4096];
        var origin = coordinate.Origin;
        if (origin.Y > MaximumY || (long)origin.Y + 15 < MinimumY) return result;
        var terrain = new Field(seed);
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int worldX = origin.X + x, worldZ = origin.Z + z;
            int height = terrain.Height(worldX, worldZ);
            Block surface = terrain.Surface(worldX, worldZ, height);
            for (int y = 0; y < 16; y++)
                result[(y * 16 + z) * 16 + x] = terrain.TerrainBlock(worldX, origin.Y + y, worldZ, height, surface);
        }

        // Roots outside this chunk are included whenever their canopy can reach it.
        // Global ordering and trunk precedence make overlapping trees independent of load order.
        foreach (var tree in terrain.Trees((long)origin.X - TreeRadius, (long)origin.X + 15 + TreeRadius,
                     (long)origin.Z - TreeRadius, (long)origin.Z + 15 + TreeRadius, cancellationToken)
                     .OrderBy(tree => tree.Priority).ThenBy(tree => tree.X).ThenBy(tree => tree.Z))
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int y = tree.Ground + 1; y <= tree.Ground + tree.Height; y++)
                Paint(tree.X, y, tree.Z, tree.Log, replaceLeaves: true);
            int crown = tree.Ground + tree.Height;
            for (int dy = -1; dy <= 2; dy++)
            {
                int radius = dy == 2 ? 1 : TreeRadius;
                for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dz) > (dy == 2 ? 1 : 3)) continue;
                    long wx = (long)tree.X + dx, wz = (long)tree.Z + dz;
                    if (wx < int.MinValue || wx > int.MaxValue || wz < int.MinValue || wz > int.MaxValue) continue;
                    Paint((int)wx, crown + dy, (int)wz, tree.Leaves, replaceLeaves: false);
                }
            }
        }
        return result;

        void Paint(int x, int y, int z, Block block, bool replaceLeaves)
        {
            long localX = (long)x - origin.X, localZ = (long)z - origin.Z;
            long localY = (long)y - origin.Y;
            if (localX is < 0 or >= 16 || localY is < 0 or >= 16 || localZ is < 0 or >= 16 ||
                y is < MinimumY or > MaximumY) return;
            int index = ((int)localY * 16 + (int)localZ) * 16 + (int)localX;
            if (result[index] == Block.Air || replaceLeaves && IsLeaves(result[index])) result[index] = block;
        }
    }

    public static int SurfaceHeight(int seed, int x, int z) => new Field(seed).Height(x, z);

    /// <summary>Independent single-cell sampling of the same global field, useful for boundary verification.</summary>
    public static Block GetBlock(int seed, Cell cell)
    {
        if (cell.Y is < MinimumY or > MaximumY) return Block.Air;
        var field = new Field(seed);
        int height = field.Height(cell.X, cell.Z);
        Block baseBlock = field.TerrainBlock(cell.X, cell.Y, cell.Z, height, field.Surface(cell.X, cell.Z, height));
        if (baseBlock != Block.Air) return baseBlock;
        Block leaves = Block.Air;
        foreach (var tree in field.Trees((long)cell.X - TreeRadius, (long)cell.X + TreeRadius,
                     (long)cell.Z - TreeRadius, (long)cell.Z + TreeRadius, default)
                     .OrderBy(tree => tree.Priority).ThenBy(tree => tree.X).ThenBy(tree => tree.Z))
        {
            if (cell.X == tree.X && cell.Z == tree.Z && cell.Y > tree.Ground && cell.Y <= tree.Ground + tree.Height)
                return tree.Log;
            int dy = cell.Y - (tree.Ground + tree.Height);
            if (dy is < -1 or > 2) continue;
            long dx = Math.Abs((long)cell.X - tree.X), dz = Math.Abs((long)cell.Z - tree.Z);
            int radius = dy == 2 ? 1 : TreeRadius;
            if (dx <= radius && dz <= radius && dx + dz <= (dy == 2 ? 1 : 3) && leaves == Block.Air)
                leaves = tree.Leaves;
        }
        return leaves;
    }

    static bool IsLeaves(Block block) => block is Block.OakLeaves or Block.BirchLeaves or Block.SpruceLeaves;

    sealed class Field
    {
        readonly TerrainNoise land, warp, climate, caves, caveDetail;
        public Field(int seed)
        {
            land = new(seed); warp = new(seed ^ 0x4f1bbcdc); climate = new(seed ^ 0x2c9277b5);
            caves = new(seed ^ 0x6a09e667); caveDetail = new(seed ^ 0x3c6ef372);
        }

        public int Height(int x, int z)
        {
            double wx = x + warp.Fractal2(x * .012 + 7, z * .012 - 4, 2) * 12;
            double wz = z + warp.Fractal2(x * .012 - 19, z * .012 + 23, 2) * 12;
            double broad = land.Fractal2(wx * .014, wz * .014, 3);
            double ridge = 1 - Math.Abs(land.Noise2(wx * .025 + 41, wz * .025 - 29));
            double natural = 16 + broad * 13 + ridge * ridge * ridge * 16;
            return Math.Clamp((int)Math.Round(9 + (natural - 9) * SpawnBlend(x, z)), 6, 43);
        }

        public Block Surface(int x, int z, int height)
        {
            if (SpawnBlend(x, z) == 0) return Block.Grass;
            double moisture = climate.Fractal2(x * .025 + 30, z * .025 - 18, 2);
            if (height >= 34) return Block.Snow;
            if (height >= 30) return Block.Stone;
            if (moisture < -.30) return climate.Noise2(x * .009, z * .009) > .25 ? Block.RedSand : Block.Sand;
            if (height < 14 && moisture > .3 && land.Unit(x, 0, z, 21) < .25) return Block.Gravel;
            return moisture > .36 ? Block.Moss : Block.Grass;
        }

        public Block TerrainBlock(int x, int y, int z, int height, Block surface)
        {
            if (y is < MinimumY or > MaximumY || y > height) return Block.Air;
            if (y == MinimumY) return Block.Bedrock;
            if (y == height) return surface;
            if (y >= height - 2)
                return surface is Block.Sand ? Block.Sandstone : surface is Block.RedSand ? Block.RedSandstone :
                    surface is Block.Snow or Block.Stone or Block.Gravel ? Block.Stone : Block.Dirt;
            if (y >= 3 && y <= height - 4 && SpawnBlend(x, z) > .2 &&
                Math.Abs(caves.Noise3(x * .12, y * .19, z * .12)) < .12 &&
                Math.Abs(caveDetail.Noise3(x * .14 + 13, y * .16 - 7, z * .14 + 5)) < .27)
                return Block.Air;
            double ore = land.Unit(x, y, z, 3);
            if (y <= 5 && ore < .012) return Block.DiamondOre;
            if (y <= 8 && ore < .025) return Block.EmeraldOre;
            if (ore < .055) return Block.GoldOre;
            if (ore < .085) return Block.RedstoneOre;
            if (ore < .115) return Block.LapisOre;
            if (ore < .175) return Block.Iron;
            if (ore < .225) return Block.CopperOre;
            if (ore < .29) return Block.Coal;
            double strata = land.Noise3(x * .07, y * .12, z * .07);
            return y <= 5 ? Block.Deepslate : strata > .4 ? Block.Granite :
                strata < -.4 ? Block.Andesite : strata > .2 && y < 12 ? Block.Tuff : Block.Stone;
        }

        public IEnumerable<Tree> Trees(long minX, long maxX, long minZ, long maxZ, CancellationToken cancellationToken)
        {
            minX = Math.Max(minX, int.MinValue); maxX = Math.Min(maxX, int.MaxValue);
            minZ = Math.Max(minZ, int.MinValue); maxZ = Math.Min(maxZ, int.MaxValue);
            long firstX = FloorDivide(minX, TreeGrid), lastX = FloorDivide(maxX, TreeGrid);
            long firstZ = FloorDivide(minZ, TreeGrid), lastZ = FloorDivide(maxZ, TreeGrid);
            for (long gx = firstX; gx <= lastX; gx++)
            for (long gz = firstZ; gz <= lastZ; gz++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int gridX = (int)gx, gridZ = (int)gz;
                if (land.Unit(gridX, 0, gridZ, 61) > .48) continue;
                long xLong = gx * TreeGrid + Math.Min(TreeGrid - 1, (int)(land.Unit(gridX, 0, gridZ, 62) * TreeGrid));
                long zLong = gz * TreeGrid + Math.Min(TreeGrid - 1, (int)(land.Unit(gridX, 0, gridZ, 63) * TreeGrid));
                if (xLong < minX || xLong > maxX || zLong < minZ || zLong > maxZ) continue;
                int x = (int)xLong, z = (int)zLong;
                if (SpawnBlend(x, z) < .7) continue;
                int height = Height(x, z);
                if (Surface(x, z, height) is not (Block.Grass or Block.Moss)) continue;
                int slope = 0;
                if (x > int.MinValue) slope = Math.Max(slope, Math.Abs(height - Height(x - 1, z)));
                if (x < int.MaxValue) slope = Math.Max(slope, Math.Abs(height - Height(x + 1, z)));
                if (z > int.MinValue) slope = Math.Max(slope, Math.Abs(height - Height(x, z - 1)));
                if (z < int.MaxValue) slope = Math.Max(slope, Math.Abs(height - Height(x, z + 1)));
                if (slope > 2) continue;
                double variation = land.Unit(x, height, z, 64);
                int trunk = 4 + Math.Min(2, (int)(land.Unit(x, height, z, 65) * 3));
                yield return new(x, z, height, trunk,
                    variation < .25 ? Block.BirchLog : variation > .75 ? Block.SpruceLog : Block.OakLog,
                    variation < .25 ? Block.BirchLeaves : variation > .75 ? Block.SpruceLeaves : Block.OakLeaves,
                    land.Unit(x, height, z, 66));
            }
        }
    }

    static long FloorDivide(long value, int divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    static double SpawnBlend(int x, int z)
    {
        double distance = Math.Max(Math.Abs(x - 20.5) - 5.5, Math.Abs(z - 21d) - 5);
        double t = Math.Clamp(distance / 9, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
