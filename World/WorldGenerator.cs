namespace BlockCircuit;

/// <summary>
/// Finite seeded terrain: domain-warped fBm, ridged hills, moisture-based surfaces,
/// intersecting 3D noise caves, and coordinate-hashed vegetation.
/// The algorithms are implemented here; FastNoiseLite is a design reference, not a dependency.
/// </summary>
public static class WorldGenerator
{
    public const int Version = 1;
    static readonly (int X, int Z)[] Adjacent = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    public static void Generate(World world, int seed)
    {
        ArgumentNullException.ThrowIfNull(world);
        var land = new TerrainNoise(seed);
        var warp = new TerrainNoise(seed ^ 0x4f1bbcdc);
        var climate = new TerrainNoise(seed ^ 0x2c9277b5);
        var caveA = new TerrainNoise(seed ^ 0x6a09e667);
        var caveB = new TerrainNoise(seed ^ 0x3c6ef372);
        int[,] heights = new int[World.Size, World.Size];

        for (int x = 0; x < World.Size; x++) for (int z = 0; z < World.Size; z++)
        {
            double wx = x + warp.Fractal2(x * .045 + 7, z * .045 - 4, 2) * 5;
            double wz = z + warp.Fractal2(x * .045 - 19, z * .045 + 23, 2) * 5;
            double broad = land.Fractal2(wx * .043, wz * .043, 4);
            double ridge = 1 - Math.Abs(land.Noise2(wx * .083 + 41, wz * .083 - 29));
            ridge = ridge * ridge * ridge;
            double mountains = Math.Clamp(.5 + climate.Noise2(x * .032 + 13, z * .032 - 7) * .65, .05, 1);
            double natural = 9 + broad * 5.5 + ridge * mountains * 12;
            double blend = SpawnBlend(x, z);
            heights[x, z] = Math.Clamp((int)Math.Round(9 + (natural - 9) * blend), 5, World.Height - 6);
        }

        for (int x = 0; x < World.Size; x++) for (int z = 0; z < World.Size; z++)
        {
            int top = heights[x, z], slope = Slope(heights, x, z);
            double moisture = climate.Fractal2(x * .065 + 30, z * .065 - 18, 3);
            bool clearing = SpawnBlend(x, z) == 0;
            Block surface = clearing ? Block.Grass
                : top >= 20 ? Block.Snow
                : top >= 17 || slope >= 3 ? Block.Stone
                : moisture < -.28 ? Block.Sand
                : moisture > .33 && land.Unit(x, 0, z, 20) < .35 ? Block.Moss
                : Block.Grass;
            for (int y = 0; y <= top; y++)
            {
                Block b = y == 0 ? Block.Bedrock
                    : y == top ? surface
                    : y >= top - 2 ? surface == Block.Sand ? Block.Sandstone : Block.Dirt
                    : Rock(land, x, y, z);
                // Preserve a roof, solid foundation under the clearing, and all bedrock.
                if (y >= 2 && y <= top - 3 && SpawnBlend(x, z) > .12)
                {
                    double n1 = caveA.Noise3(x * .15, y * .21, z * .15);
                    double n2 = caveB.Noise3(x * .17 + 13, y * .19 - 7, z * .17 + 5);
                    bool tunnel = Math.Abs(n1) < .17 && Math.Abs(n2) < .29;
                    bool chamber = caveA.Noise3(x * .085 + 31, y * .14 + 9, z * .085 - 18) > .53;
                    if (tunnel || chamber) b = Block.Air;
                }
                world.Set(new(x, y, z), b);
            }
        }
        GrowTrees(world, heights, land, climate);
    }

    static Block Rock(TerrainNoise noise, int x, int y, int z)
    {
        double ore = noise.Unit(x, y, z, 3);
        if (y <= 4 && ore < .012) return Block.DiamondOre;
        if (y <= 6 && ore < .022) return Block.EmeraldOre;
        if (ore < .055) return Block.GoldOre;
        if (ore < .085) return Block.RedstoneOre;
        if (ore < .115) return Block.LapisOre;
        if (ore < .175) return Block.Iron;
        if (ore < .225) return Block.CopperOre;
        if (ore < .29) return Block.Coal;
        double strata = noise.Noise3(x * .09, y * .18, z * .09);
        return y <= 2 ? Block.Deepslate
            : strata > .4 ? Block.Granite
            : strata < -.4 ? Block.Andesite
            : strata > .2 && y < 7 ? Block.Tuff : Block.Stone;
    }

    static void GrowTrees(World world, int[,] heights, TerrainNoise noise, TerrainNoise climate)
    {
        bool[,] occupied = new bool[World.Size, World.Size];
        for (int x = 3; x < World.Size - 3; x++) for (int z = 3; z < World.Size - 3; z++)
        {
            int ground = heights[x, z];
            if (occupied[x, z] || SpawnBlend(x, z) < .4 || Slope(heights, x, z) > 1 ||
                ground > World.Height - 9 || world.Get(new(x, ground, z)) is not (Block.Grass or Block.Moss)) continue;
            double moisture = climate.Fractal2(x * .065 + 30, z * .065 - 18, 3);
            double chance = .035 + Math.Max(0, moisture) * .055;
            if (noise.Unit(x, ground, z, 41) > chance) continue;
            bool pine = ground >= 14 || moisture > .28;
            int trunk = 3 + (int)(noise.Unit(x, ground, z, 42) * 2.99);
            bool birch = !pine && noise.Unit(x, ground, z, 43) > .6;
            Block log = pine ? Block.SpruceLog : birch ? Block.BirchLog : Block.OakLog;
            Block leaves = pine ? Block.SpruceLeaves : birch ? Block.BirchLeaves : Block.OakLeaves;
            for (int y = ground + 1; y <= ground + trunk; y++) world.Set(new(x, y, z), log);
            int crown = ground + trunk;
            for (int dy = -2; dy <= 2; dy++)
            {
                int radius = pine ? (dy <= 0 ? 2 : dy == 1 ? 1 : 0) : dy == 2 ? 1 : 2;
                for (int dx = -radius; dx <= radius; dx++) for (int dz = -radius; dz <= radius; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius + 1) continue;
                    // Irregular outer leaves soften the repeated spherical canopy shape.
                    if (!pine && Math.Abs(dx) + Math.Abs(dz) >= 3 && noise.Unit(x + dx, crown + dy, z + dz, 44) < .22) continue;
                    var p = new Cell(x + dx, crown + dy, z + dz);
                    if (world.Inside(p) && world.Get(p) == Block.Air) world.Set(p, leaves);
                }
            }
            for (int dx = -3; dx <= 3; dx++) for (int dz = -3; dz <= 3; dz++)
                occupied[x + dx, z + dz] = true;
        }
    }

    static int Slope(int[,] heights, int x, int z)
    {
        int top = heights[x, z], slope = 0;
        foreach (var (dx, dz) in Adjacent)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nx < World.Size && nz >= 0 && nz < World.Size)
                slope = Math.Max(slope, Math.Abs(top - heights[nx, nz]));
        }
        return slope;
    }

    static double SpawnBlend(int x, int z)
    {
        double distance = Math.Max(Math.Abs(x - 20.5) - 5.5, Math.Abs(z - 21.0) - 5);
        double t = Math.Clamp(distance / 5, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
