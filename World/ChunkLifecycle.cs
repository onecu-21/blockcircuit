namespace BlockCircuit;

/// <summary>CPU chunk readiness; transitions are owned by the simulation/render thread.</summary>
public enum ChunkState { Unloaded, Loading, Generated, Meshing, Ready, Unloading }

/// <summary>Operation tokens prevent cancelled or superseded jobs from publishing stale results.</summary>
public sealed class ChunkLifecycle
{
    static long nextToken;
    long activeToken;
    ChunkState rollbackState;
    public ChunkState State { get; private set; }
    public Exception? LastError { get; private set; }

    public ChunkLifecycle(ChunkState initialState = ChunkState.Unloaded)
    {
        if (initialState is not (ChunkState.Unloaded or ChunkState.Generated or ChunkState.Ready))
            throw new ArgumentOutOfRangeException(nameof(initialState), "An initial state must be stable.");
        State = initialState;
    }

    public long BeginLoading() => Begin(ChunkState.Unloaded, ChunkState.Loading, ChunkState.Unloaded);

    public long BeginMeshing()
    {
        if (State is not (ChunkState.Generated or ChunkState.Ready))
            throw new InvalidOperationException($"Cannot begin meshing from {State}.");
        // A fresh renderer may upload a Ready chunk again. Failed uploads retry from Generated.
        return Begin(State, ChunkState.Meshing, ChunkState.Generated);
    }

    public long BeginUnloading()
    {
        if (State is not (ChunkState.Generated or ChunkState.Ready))
            throw new InvalidOperationException($"Cannot begin unloading from {State}.");
        return Begin(State, ChunkState.Unloading, State);
    }

    public bool CompleteLoading(long token) => Complete(token, ChunkState.Loading, ChunkState.Generated);
    public bool CompleteMeshing(long token) => Complete(token, ChunkState.Meshing, ChunkState.Ready);
    public bool CompleteUnloading(long token) => Complete(token, ChunkState.Unloading, ChunkState.Unloaded);

    public bool Cancel(long token)
    {
        if (token == 0 || token != activeToken) return false;
        State = rollbackState;
        activeToken = 0;
        return true;
    }

    public bool Fail(long token, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Cancel(token)) return false;
        LastError = error;
        return true;
    }

    /// <summary>Mesh invalidation leaves the resident payload ready for a new build.</summary>
    public void Invalidate()
    {
        if (State is ChunkState.Unloaded or ChunkState.Loading)
            throw new InvalidOperationException($"Cannot invalidate a nonresident chunk in {State}.");
        activeToken = 0;
        State = ChunkState.Generated;
    }

    internal void Retire()
    {
        if (activeToken != 0) Cancel(activeToken);
        if (State == ChunkState.Unloaded) return;
        long token = BeginUnloading();
        CompleteUnloading(token);
    }

    long Begin(ChunkState expected, ChunkState running, ChunkState rollback)
    {
        if (State != expected) throw new InvalidOperationException($"Cannot begin {running} from {State}.");
        long token = Interlocked.Increment(ref nextToken);
        if (token <= 0) throw new OverflowException("Chunk operation token space exhausted.");
        rollbackState = rollback;
        activeToken = token;
        LastError = null;
        State = running;
        return token;
    }

    bool Complete(long token, ChunkState expected, ChunkState completed)
    {
        if (token == 0 || token != activeToken || State != expected) return false;
        State = completed;
        activeToken = 0;
        LastError = null;
        return true;
    }
}
