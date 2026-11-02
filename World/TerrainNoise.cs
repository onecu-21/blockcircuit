namespace BlockCircuit;

/// <summary>Seeded, coordinate-addressable value noise. No mutable RNG state or external dependencies.</summary>
public sealed class TerrainNoise(int seed)
{
    readonly uint seedBits = unchecked((uint)seed);

    public double Unit(int x, int y, int z, int stream = 0)
    {
        uint h = Mix(seedBits ^ unchecked((uint)stream * 0x9e3779b9u));
        h = Mix(h ^ unchecked((uint)x * 0x85ebca6bu));
        h = Mix(h ^ unchecked((uint)y * 0xc2b2ae35u));
        h = Mix(h ^ unchecked((uint)z * 0x27d4eb2fu));
        return (h >> 8) / 16777215.0;
    }

    public double Noise2(double x, double z) => Noise3(x, 0, z);

    public double Noise3(double x, double y, double z)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        double tx = Fade(x - ix), ty = Fade(y - iy), tz = Fade(z - iz);
        double a = Lerp(Unit(ix, iy, iz), Unit(ix + 1, iy, iz), tx);
        double b = Lerp(Unit(ix, iy + 1, iz), Unit(ix + 1, iy + 1, iz), tx);
        double c = Lerp(Unit(ix, iy, iz + 1), Unit(ix + 1, iy, iz + 1), tx);
        double d = Lerp(Unit(ix, iy + 1, iz + 1), Unit(ix + 1, iy + 1, iz + 1), tx);
        return Lerp(Lerp(a, b, ty), Lerp(c, d, ty), tz) * 2 - 1;
    }

    public double Fractal2(double x, double z, int octaves = 4)
    {
        if (octaves is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(octaves));
        double sum = 0, amplitude = 1, total = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Noise2(x, z) * amplitude;
            total += amplitude;
            x = x * 2.03 + 17.17;
            z = z * 2.03 - 11.73;
            amplitude *= .5;
        }
        return sum / total;
    }

    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static uint Mix(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            return x ^ (x >> 16);
        }
    }
}
