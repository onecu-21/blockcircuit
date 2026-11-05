using System.Diagnostics;
using System.Numerics;

namespace BlockCircuit;

public sealed record StreamingOptions
{
    public int MaxLoadJobs { get; init; } = 2;
    public int MaxIntegrationsPerFrame { get; init; } = 1;
    public int MaxUnloadsPerFrame { get; init; } = 1;
    public int MaxSaveCapturesPerFrame { get; init; } = 2;
    public int UnloadMargin { get; init; } = 1;
    public int MaxResidentColumns { get; init; } = 256;
    public long MaximumColumnBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>Owner-thread residency with bounded, snapshot-only background load/generation jobs.</summary>
public sealed class WorldStreamer : IDisposable
{
    readonly World world;
    readonly string path;
    readonly StreamingOptions options;
    readonly int ownerThread = Environment.CurrentManagedThreadId;
    readonly Dictionary<ColumnCoord, LoadJob> jobs = [];
    readonly HashSet<ColumnCoord> loaded = [];
    readonly Dictionary<ColumnCoord, long> failures = [];
    readonly StreamingSaveSession saves;
    bool disposed;
    long frame;
    long lastAutosaveTick;
    internal string StoragePath => path;
    public int PendingJobs => jobs.Count;
    public int ResidentColumnCount => loaded.Count;
    public IReadOnlyCollection<ColumnCoord> LoadedColumns => loaded.ToArray();
    public int GenerationJobsStarted { get; private set; }
    public int CancelledJobs { get; private set; }
    public int UnloadedColumns { get; private set; }
    public Exception? LastError { get; private set; }
    public bool SaveInProgress => saves.Busy;
    public int MaximumResidentColumns => options.MaxResidentColumns;
    public int LastIntegrations { get; private set; }
    public int LastUnloads { get; private set; }
    public int LastSaveCaptures => saves.LastCaptures;
    public double LastUpdateMilliseconds { get; private set; }
    sealed record LoadedColumn(ColumnCoord Coord, StreamingChunkData[] Chunks);
    sealed record LoadJob(CancellationTokenSource Cancellation, Task<LoadedColumn> Task, ChunkFileReference[] References);

    public WorldStreamer(World world, string savePath, StreamingOptions? options = null)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        path = Path.GetFullPath(savePath); this.options = options ?? new();
        if (this.options.MaxLoadJobs is < 1 or > 16 || this.options.MaxIntegrationsPerFrame < 1 ||
            this.options.MaxUnloadsPerFrame < 1 || this.options.MaxSaveCapturesPerFrame < 1 ||
            this.options.UnloadMargin < 0 || this.options.MaxResidentColumns < 1 || this.options.MaximumColumnBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (world.ActiveStreamer is not null) throw new InvalidOperationException("World already has a streaming owner.");
        world.ActiveStreamer = this;
        world.EnableStreaming();
        loaded.UnionWith(world.Chunks.Chunks.Keys.Select(ColumnCoord.FromChunk));
        world.KnownColumns.UnionWith(loaded); world.AvailableColumns.UnionWith(loaded);
        saves = new(world, path); lastAutosaveTick = world.TickCount;
    }

    public void Update(Vector3 player, int radiusChunks = 2, float yaw = 0, float pitch = 0)
    {
        CheckOwner(); ObjectDisposedException.ThrowIf(disposed, this);
        CollisionSystem.RequireFinite(player, nameof(player));
        if (Math.Abs(player.X) > World.MaximumPlayerCoordinate || Math.Abs(player.Z) > World.MaximumPlayerCoordinate || !float.IsFinite(yaw) || !float.IsFinite(pitch)) throw new ArgumentOutOfRangeException(nameof(player));
        if (radiusChunks is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(radiusChunks));
        long started = Stopwatch.GetTimestamp();
        frame++; LastIntegrations = LastUnloads = 0;
        var center = ColumnCoord.FromPosition(player);
        int unloadRadius = radiusChunks + options.UnloadMargin;
        bool Wanted(ColumnCoord column) => Distance(column, center) <= radiusChunks;
        foreach (var pair in jobs)
            if (!Wanted(pair.Key) && !pair.Value.Cancellation.IsCancellationRequested)
            { pair.Value.Cancellation.Cancel(); CancelledJobs++; }

        world.RetainedChunkFiles.Clear();
        foreach (var job in jobs.Values) world.RetainedChunkFiles.UnionWith(job.References.Select(e => e.File));
        saves.UpdatePlayer(player, yaw, pitch);
        saves.Pump(options.MaxSaveCapturesPerFrame);
        if (saves.LastError is not null) LastError = saves.LastError;
        foreach (var pair in jobs.Where(p => p.Value.Task.IsCompleted).OrderBy(p => Distance(p.Key, center)).ToArray())
        {
            if (LastIntegrations >= options.MaxIntegrationsPerFrame) break;
            var job = pair.Value; jobs.Remove(pair.Key);
            try
            {
                var result = job.Task.GetAwaiter().GetResult();
                if (job.Cancellation.IsCancellationRequested || !Wanted(pair.Key)) continue;
                foreach (var data in result.Chunks) world.ImportChunk(data, dirty: !job.References.Any(e => e.Coord == data.Coord));
                loaded.Add(pair.Key); world.AvailableColumns.Add(pair.Key); world.KnownColumns.Add(pair.Key);
                // A loaded neighbor may now support or release blocks waiting at the frontier.
                foreach (var coord in world.Chunks.Chunks.Keys.Where(c => ColumnCoord.FromChunk(c) == pair.Key))
                    world.FallingBlocks.OnChunkLoaded(coord);
                world.Circuits.ProcessPending(); LastIntegrations++; failures.Remove(pair.Key);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { LastError = error; failures[pair.Key] = frame + 60; }
            finally { job.Cancellation.Dispose(); }
        }

        var pinned = world.FallingBlocks.Active.Where(a => a.Position.Y < 258 && world.Registries.Blocks.TryGetBlock(a.PersistentId, out _)).Select(a => ColumnCoord.FromPosition(a.Position)).ToHashSet();
        var dirtyColumns = world.Chunks.DirtyCoordinates.Select(ColumnCoord.FromChunk).ToHashSet();
        foreach (var failed in failures.Where(p => p.Value < frame && Distance(p.Key, center) > unloadRadius).Select(p => p.Key).ToArray()) failures.Remove(failed);
        bool needsSave = false;
        foreach (var column in loaded.OrderByDescending(c => Distance(c, center)).ToArray())
        {
            long distance = Distance(column, center);
            bool pressure = loaded.Count + jobs.Count >= options.MaxResidentColumns;
            bool centerMissing = !loaded.Contains(center);
            if (pinned.Contains(column) || column == center ||
                (distance <= unloadRadius && !pressure) || (distance <= radiusChunks && !(pressure && centerMissing))) continue;
            if (dirtyColumns.Contains(column))
            { needsSave = true; continue; }
            if (LastUnloads == options.MaxUnloadsPerFrame) continue;
            world.EvictColumn(column); loaded.Remove(column); UnloadedColumns++; LastUnloads++;
        }
        if (!saves.Busy && (needsSave || world.TickCount - lastAutosaveTick >= 200))
        { saves.Request(player, yaw, pitch); lastAutosaveTick = world.TickCount; }

        var wanted = new PriorityQueue<ColumnCoord, long>();
        for (int dz = -radiusChunks; dz <= radiusChunks; dz++)
        for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
        {
            var column = new ColumnCoord(center.X + dx, center.Z + dz);
            if (!new ChunkCoord(column.X, 0, column.Z).IsValid || loaded.Contains(column) || jobs.ContainsKey(column) ||
                failures.TryGetValue(column, out long retry) && frame < retry) continue;
            wanted.Enqueue(column, (long)dx * dx + (long)dz * dz);
        }
        while (jobs.Count < options.MaxLoadJobs && loaded.Count + jobs.Count < options.MaxResidentColumns && wanted.TryDequeue(out var column, out _))
            BeginLoad(column);
        LastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    void BeginLoad(ColumnCoord column)
    {
        var references = world.StoredChunkReferences.Values.Where(e => ColumnCoord.FromChunk(e.Coord) == column && e.Coord.Y is >= 0 and <= 15).ToArray();
        bool known = world.KnownColumns.Contains(column);
        int seed = world.Seed;
        string? storePath = world.StoredPath;
        var registries = world.Registries;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        if (!known) GenerationJobsStarted++;
        Task<LoadedColumn> task = Task.Run(() =>
        {
            var chunks = new List<StreamingChunkData>();
            long totalBytes = 0;
            foreach (var entry in references)
            {
                if (storePath is null) throw new InvalidDataException("Missing source for saved streamed chunk.");
                totalBytes = checked(totalBytes + new FileInfo(Path.Combine(storePath + ".chunks", entry.File)).Length);
                if (totalBytes > options.MaximumColumnBytes) throw new InvalidDataException("Saved column exceeds its configured byte budget.");
                token.ThrowIfCancellationRequested();
                if (storePath is null) throw new InvalidDataException("Missing source for saved streamed chunk.");
                chunks.Add(ChunkSaveStore.ReadStreamingChunk(storePath, entry, registries, token));
            }
            if (!known)
                for (int y = 0; y <= StreamingTerrainGenerator.MaximumY / 16; y++)
                {
                    var coord = new ChunkCoord(column.X, y, column.Z);
                    if (references.Any(e => e.Coord == coord)) continue;
                    token.ThrowIfCancellationRequested();
                    chunks.Add(new(coord, StreamingTerrainGenerator.Generate(seed, coord, token), [], []));
                }
            token.ThrowIfCancellationRequested();
            return new LoadedColumn(column, chunks.ToArray());
        }, token);
        jobs.Add(column, new(cancellation, task, references));
    }
    public void FlushSaves(Vector3 position, float yaw, float pitch)
    {
        CheckOwner(); ObjectDisposedException.ThrowIf(disposed, this);
        world.RetainedChunkFiles.Clear();
        foreach (var job in jobs.Values) world.RetainedChunkFiles.UnionWith(job.References.Select(e => e.File));
        saves.Flush(position, yaw, pitch);
        LastError = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        CheckOwner(); disposed = true;
        foreach (var job in jobs.Values) job.Cancellation.Cancel();
        foreach (var job in jobs.Values)
        { try { job.Task.GetAwaiter().GetResult(); } catch { } job.Cancellation.Dispose(); }
        jobs.Clear(); saves.Dispose(); world.RetainedChunkFiles.Clear();
        if (ReferenceEquals(world.ActiveStreamer, this)) world.ActiveStreamer = null;
    }
    void CheckOwner()
    { if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Streaming residency must be updated on its owner thread."); }
    static long Distance(ColumnCoord a, ColumnCoord b) => Math.Max(Math.Abs((long)a.X - b.X), Math.Abs((long)a.Z - b.Z));
}
