namespace BlockCircuit;

public sealed record ChunkMeshResult(ChunkCoord Coord, Chunk SourceChunk, long Revision,
    long OperationToken, ChunkMeshData Data, int WorkerThreadId);

/// <summary>Bounded CPU-only mesh jobs. All capture, lifecycle and completion decisions belong to the creating thread.</summary>
public sealed class ChunkMeshScheduler : IDisposable
{
    readonly int ownerThread = Environment.CurrentManagedThreadId;
    readonly List<Job> jobs = [];
    bool disposed;
    public int MaximumJobs { get; }
    public int PendingCount => jobs.Count;
    public Exception? LastError { get; private set; }
    public long TotalScheduled { get; private set; }
    public long TotalCompleted { get; private set; }
    public long TotalDiscarded { get; private set; }

    public ChunkMeshScheduler(int maximumJobs = 2)
    {
        if (maximumJobs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        MaximumJobs = maximumJobs;
    }

    public bool Request(World world, ChunkCoord coord, Chunk chunk)
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(chunk);
        if (!coord.IsValid || chunk.Coord != coord) throw new ArgumentException("Chunk coordinate mismatch.", nameof(coord));
        CancelObsolete(world);
        if (jobs.Count >= MaximumJobs || !IsCurrent(world, world.Chunks, coord, chunk, chunk.Revision) ||
            chunk.Lifecycle.State is not (ChunkState.Generated or ChunkState.Ready)) return false;
        if (jobs.Any(job => job.Coord == coord && ReferenceEquals(job.SourceChunk, chunk) && !job.Cancelled)) return false;

        long operation = 0;
        CancellationTokenSource? cancellation = null;
        try
        {
            var snapshot = ChunkMesher.Capture(world, coord);
            long revision = chunk.Revision;
            operation = chunk.Lifecycle.BeginMeshing();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            // Capture only immutable snapshot and cancellation state in the worker closure.
            Task<BuiltMesh> task = Task.Run(() => new BuiltMesh(
                ChunkMesher.Build(snapshot, cancellationToken: token), Environment.CurrentManagedThreadId), token);
            jobs.Add(new(world, world.Chunks, coord, chunk, revision, operation, cancellation, task));
            TotalScheduled++;
            return true;
        }
        catch (Exception error)
        {
            cancellation?.Dispose();
            chunk.Lifecycle.Fail(operation, error);
            LastError = error;
            return false;
        }
    }

    public void CancelObsolete(World world)
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(world);
        foreach (var job in jobs)
            if (!ReferenceEquals(job.World, world) || !IsCurrent(world, job.Manager, job.Coord, job.SourceChunk, job.Revision) ||
                job.SourceChunk.Lifecycle.State != ChunkState.Meshing)
                Cancel(job);
        DrainCancelledCompleted();
    }

    public bool TryTakeCompleted(World world, out ChunkMeshResult result)
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(world);
        CancelObsolete(world);
        for (int i = 0; i < jobs.Count;)
        {
            var job = jobs[i];
            if (!job.Task.IsCompleted) { i++; continue; }
            jobs.RemoveAt(i);
            bool valid = !job.Cancelled && ReferenceEquals(job.World, world) &&
                IsCurrent(world, job.Manager, job.Coord, job.SourceChunk, job.Revision) &&
                job.SourceChunk.Lifecycle.State == ChunkState.Meshing;
            BuiltMesh? built = Observe(job);
            if (valid && built is not null)
            {
                TotalCompleted++;
                result = new(job.Coord, job.SourceChunk, job.Revision, job.OperationToken, built.Data, built.ThreadId);
                // Renderer completes the operation only after the owner thread uploads its GPU buffers.
                return true;
            }
            job.SourceChunk.Lifecycle.Cancel(job.OperationToken);
            TotalDiscarded++;
        }
        result = null!;
        return false;
    }

    public void Reset()
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var job in jobs) Cancel(job);
        // Running jobs remain counted until their cancellation is observed.
        DrainCancelledCompleted();
        LastError = null;
    }

    public void Dispose()
    {
        RequireOwner();
        if (disposed) return;
        disposed = true;
        foreach (var job in jobs) Cancel(job);
        foreach (var job in jobs)
        {
            Observe(job);
            job.SourceChunk.Lifecycle.Cancel(job.OperationToken);
            TotalDiscarded++;
        }
        jobs.Clear();
    }

    static bool IsCurrent(World world, ChunkManager manager, ChunkCoord coord, Chunk chunk, long revision) =>
        ReferenceEquals(world.Chunks, manager) && world.Chunks.Chunks.TryGetValue(coord, out var current) &&
        ReferenceEquals(current, chunk) && current.Revision == revision;

    void Cancel(Job job)
    {
        if (job.Cancelled) return;
        job.Cancelled = true;
        job.Cancellation.Cancel();
        job.SourceChunk.Lifecycle.Cancel(job.OperationToken);
    }

    void DrainCancelledCompleted()
    {
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            var job = jobs[i];
            if (!job.Cancelled || !job.Task.IsCompleted) continue;
            jobs.RemoveAt(i);
            Observe(job);
            TotalDiscarded++;
        }
    }

    BuiltMesh? Observe(Job job)
    {
        try { return job.Task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            LastError = error;
            job.SourceChunk.Lifecycle.Fail(job.OperationToken, error);
            return null;
        }
        finally { job.Cancellation.Dispose(); }
    }

    void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Mesh scheduler must be controlled by its creating thread.");
    }

    sealed record BuiltMesh(ChunkMeshData Data, int ThreadId);
    sealed class Job(World world, ChunkManager manager, ChunkCoord coord, Chunk sourceChunk, long revision,
        long operationToken, CancellationTokenSource cancellation, Task<BuiltMesh> task)
    {
        public World World { get; } = world;
        public ChunkManager Manager { get; } = manager;
        public ChunkCoord Coord { get; } = coord;
        public Chunk SourceChunk { get; } = sourceChunk;
        public long Revision { get; } = revision;
        public long OperationToken { get; } = operationToken;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<BuiltMesh> Task { get; } = task;
        public bool Cancelled { get; set; }
    }
}
