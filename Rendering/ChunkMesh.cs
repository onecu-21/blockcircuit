using System.Numerics;
using Raylib_cs;

namespace BlockCircuit;

/// <summary>
/// A persistent, non-indexed GPU mesh. The capacity is independent of the active
/// vertex count, so an edited chunk can reuse all three vertex buffers.
/// Created, updated, drawn and disposed only on the render thread.
/// </summary>
public sealed unsafe class ChunkMesh : IDisposable
{
    Mesh mesh;
    int capacity;
    bool disposed;

    public int VertexCount => mesh.VertexCount;
    public int TriangleCount => mesh.TriangleCount;
    public int Capacity => capacity;
    public int BufferCount => capacity == 0 ? 0 : 3;
    public long UploadCount { get; private set; }
    public long AllocationCount { get; private set; }
    public long ReleasedMeshCount { get; private set; }

    public void Update(CpuMeshData data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        int count = data.VertexCount;
        if (count % 3 != 0 || data.Positions.Length != count * 3 ||
            data.TexCoords.Length != count * 2 || data.Colors.Length != count * 4)
            throw new ArgumentException("Malformed triangle mesh.", nameof(data));

        if (count == 0)
        {
            // Keep capacity for the next edit, but issue no draw or GPU upload.
            mesh.VertexCount = mesh.TriangleCount = 0;
            return;
        }

        if (count > capacity)
        {
            int nextCapacity = Math.Max(96, capacity);
            while (nextCapacity < count) nextCapacity = checked(nextCapacity * 2);
            Mesh replacement = default;
            try
            {
                replacement.VertexCount = nextCapacity;
                replacement.TriangleCount = nextCapacity / 3;
                // Allocate through raylib: UnloadMesh uses the same allocator.
                replacement.Vertices = Allocate<float>(nextCapacity * 3);
                replacement.TexCoords = Allocate<float>(nextCapacity * 2);
                replacement.Colors = Allocate<byte>(nextCapacity * 4);
                Copy(data, ref replacement);
                Raylib.UploadMesh(ref replacement, false);
                if (replacement.VaoId == 0 || replacement.VboId == null)
                    throw new InvalidOperationException("Failed to upload a chunk mesh.");
            }
            catch
            {
                Raylib.UnloadMesh(replacement);
                throw;
            }

            if (capacity != 0)
            {
                Raylib.UnloadMesh(mesh);
                ReleasedMeshCount++;
            }
            mesh = replacement;
            capacity = nextCapacity;
            AllocationCount++;
        }
        else
        {
            Copy(data, ref mesh);
            Raylib.UpdateMeshBuffer(mesh, 0, mesh.Vertices, count * 3 * sizeof(float), 0);
            Raylib.UpdateMeshBuffer(mesh, 1, mesh.TexCoords, count * 2 * sizeof(float), 0);
            Raylib.UpdateMeshBuffer(mesh, 3, mesh.Colors, count * 4, 0);
        }

        mesh.VertexCount = count;
        mesh.TriangleCount = count / 3;
        UploadCount++;
    }

    static T* Allocate<T>(int count) where T : unmanaged
    {
        T* pointer = (T*)Raylib.MemAlloc(checked((uint)(count * sizeof(T))));
        if (pointer == null) throw new OutOfMemoryException();
        new Span<T>(pointer, count).Clear();
        return pointer;
    }

    static void Copy(CpuMeshData source, ref Mesh target)
    {
        source.Positions.AsSpan().CopyTo(new Span<float>(target.Vertices, source.Positions.Length));
        source.TexCoords.AsSpan().CopyTo(new Span<float>(target.TexCoords, source.TexCoords.Length));
        source.Colors.AsSpan().CopyTo(new Span<byte>(target.Colors, source.Colors.Length));
    }

    public void Draw(Material material)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mesh.VertexCount != 0) Raylib.DrawMesh(mesh, material, Matrix4x4.Identity);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (capacity != 0)
        {
            Raylib.UnloadMesh(mesh);
            ReleasedMeshCount++;
        }
        mesh = default;
        capacity = 0;
        disposed = true;
    }
}
