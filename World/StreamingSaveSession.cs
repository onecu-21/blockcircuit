using System.Numerics;

namespace BlockCircuit;

internal sealed class StreamingSaveSession : IDisposable
{
    readonly World world;
    readonly string path;
    readonly Dictionary<ChunkCoord, (long Revision, ChunkFileData? Data)> captured = [];
    Task<Receipt>? task;
    CancellationTokenSource? cancellation;
    bool capturing;
    Vector3 position;
    float yaw, pitch;
    public Exception? LastError { get; private set; }
    public bool Busy => capturing || task is not null;
    public int LastCaptures { get; private set; }
    sealed record Receipt(string Generation, ChunkFileReference[] References, ChunkSaveReport Report,
        Dictionary<ChunkCoord, long> Revisions);
    public StreamingSaveSession(World world, string path) { this.world = world; this.path = path; }
    public void UpdatePlayer(Vector3 player, float facing, float elevation)
    { position = player; yaw = facing; pitch = elevation; }
    public void Request(Vector3 player, float facing = 0, float elevation = 0)
    {
        position = player; yaw = facing; pitch = elevation;
        if (Busy) return;
        captured.Clear(); capturing = true; LastError = null;
    }
    public void Pump(int maximumCaptures)
    {
        LastCaptures = 0;
        if (task?.IsCompleted == true)
        {
            try
            {
                var result = task.GetAwaiter().GetResult();
                world.StoredPath = path; world.StoredGeneration = result.Generation;
                world.StoredChunkReferences.Clear();
                foreach (var entry in result.References) world.StoredChunkReferences.Add(entry.Coord, entry);
                foreach (var pair in result.Revisions)
                {
                    if (world.Chunks.Chunks.ContainsKey(pair.Key) && world.StoredChunkReferences.TryGetValue(pair.Key, out var saved) &&
                        ChunkSaveStore.SwitchSignature(world, pair.Key) != saved.SwitchSignature) world.Chunks.MarkContentDirty(pair.Key.Origin);
                    else world.Chunks.MarkSaved(pair.Key, pair.Value);
                }
                world.LastSaveReport = result.Report; world.LastLoadRecovered = false;
            }
            catch (Exception error) { LastError = error; }
            finally { task = null; cancellation?.Dispose(); cancellation = null; }
        }
        if (!capturing) return;
        foreach (var pair in world.Chunks.Chunks)
        {
            string signature = ChunkSaveStore.SwitchSignature(world, pair.Key);
            if (captured.TryGetValue(pair.Key, out var before) && before.Data is not null && before.Revision == world.Chunks.GetContentRevision(pair.Key))
            {
                string capturedSignature = string.Join(";", before.Data.Switches.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).Select(c => $"{c.X},{c.Y},{c.Z}"));
                if (capturedSignature != signature) world.Chunks.MarkContentDirty(pair.Key.Origin);
            }
            else if (!pair.Value.IsDirty && world.StoredChunkReferences.TryGetValue(pair.Key, out var saved) && saved.SwitchSignature != signature)
                world.Chunks.MarkContentDirty(pair.Key.Origin);
        }
        var dirty = world.Chunks.DirtyCoordinates;
        foreach (var coord in dirty)
        {
            long revision = world.Chunks.GetContentRevision(coord);
            if (captured.TryGetValue(coord, out var prior) && prior.Revision == revision) continue;
            if (LastCaptures == maximumCaptures) return;
            captured[coord] = (revision, world.Chunks.Chunks.ContainsKey(coord) ? ChunkSaveStore.CaptureChunk(world, coord) : null);
            LastCaptures++;
        }
        // All captured voxel states still match this metadata/falling-state snapshot.
        if (world.Chunks.DirtyCoordinates.Any(c => !captured.TryGetValue(c, out var data) || data.Revision != world.Chunks.GetContentRevision(c))) return;
        var detached = new World(false, registries: world.Registries);
        detached.CopyMetadataFrom(world);
        var falling = world.FallingBlocks.Capture();
        var capturedChunks = captured.ToArray();
        var versions = captured.ToDictionary(p => p.Key, p => p.Value.Revision);
        captured.Clear(); capturing = false;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var savedPosition = position; float savedYaw = yaw, savedPitch = pitch;
        task = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in capturedChunks)
            {
                token.ThrowIfCancellationRequested();
                if (item.Value.Data is not null) detached.ImportChunk(StreamingChunkData.Decode(detached.Registries, item.Value.Data), dirty: true);
                else detached.Chunks.RecordDeletion(item.Key);
            }
            detached.FallingBlocks.Restore(falling);
            ChunkSaveStore.Save(detached, path, savedPosition, savedYaw, savedPitch, _ => token.ThrowIfCancellationRequested());
            return new Receipt(detached.StoredGeneration!, detached.StoredChunkReferences.Values.ToArray(), detached.LastSaveReport, versions);
        }, token);
    }
    public void Flush(Vector3 player, float facing, float elevation)
    {
        while (Busy)
        {
            Pump(int.MaxValue);
            if (task is not null) try { task.GetAwaiter().GetResult(); } catch { }
        }
        if (LastError is not null) throw new IOException("Background chunk save failed.", LastError);
        Request(player, facing, elevation);
        while (Busy)
        {
            Pump(int.MaxValue);
            if (task is not null) try { task.GetAwaiter().GetResult(); } catch { }
        }
        if (LastError is not null) throw new IOException("Background chunk save failed.", LastError);
    }
    public void Dispose()
    {
        capturing = false; captured.Clear();
        cancellation?.Cancel();
        if (task is not null) try { task.GetAwaiter().GetResult(); } catch { }
        task = null; cancellation?.Dispose(); cancellation = null;
    }
}
