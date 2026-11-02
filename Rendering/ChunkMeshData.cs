namespace BlockCircuit;

/// <summary>Headless mesh output; every quad is two counterclockwise triangles.</summary>
public sealed class CpuMeshData
{
    public static CpuMeshData Empty { get; } = new([], [], []);

    public float[] Positions { get; }
    public float[] TexCoords { get; }
    // R/G contain raw atlas tile coordinates (0..31), B contains lighting, A is 255.
    public byte[] Colors { get; }
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => VertexCount / 3;
    public int QuadCount => VertexCount / 6;

    public CpuMeshData(float[] positions, float[] texCoords, byte[] colors)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(texCoords);
        ArgumentNullException.ThrowIfNull(colors);
        if (positions.Length % 18 != 0 || texCoords.Length != positions.Length / 3 * 2 ||
            colors.Length != positions.Length / 3 * 4)
            throw new ArgumentException("Mesh attributes must describe complete unindexed quads.");
        Positions = positions;
        TexCoords = texCoords;
        Colors = colors;
    }
}

/// <summary>Opaque and translucent passes plus the small dynamic circuit models.</summary>
public sealed class ChunkMeshData
{
    public CpuMeshData Opaque { get; }
    public CpuMeshData Transparent { get; }
    public Cell[] Circuits { get; }
    public int ExposedFaceCount { get; }
    public int QuadCount => Opaque.QuadCount + Transparent.QuadCount;

    public ChunkMeshData(CpuMeshData opaque, CpuMeshData transparent, Cell[] circuits, int exposedFaceCount)
    {
        Opaque = opaque;
        Transparent = transparent;
        Circuits = circuits;
        ExposedFaceCount = exposedFaceCount;
    }
}
