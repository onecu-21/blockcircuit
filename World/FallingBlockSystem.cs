using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Focused falling-block state; coordinates are X/Z centers and the block's bottom Y.</summary>
public sealed class FallingBlock
{
    public Guid Id { get; internal set; }
    public ContentId PersistentId { get; internal set; }
    public Block Block { get; internal set; }
    public Vector3 Position { get; internal set; }
    public Vector3 PreviousPosition { get; internal set; }
    /// <summary>Vertical displacement per 50 ms simulation tick.</summary>
    public float VelocityY { get; internal set; }
    public JsonElement? Data { get; internal set; }
    internal FallingBlock(Guid id, ContentId persistentId, Block block, Vector3 position, Vector3 previousPosition, float velocityY, JsonElement? data)
    { Id = id; PersistentId = persistentId; Block = block; Position = position; PreviousPosition = previousPosition; VelocityY = velocityY; Data = data; }
}

public sealed record FallingBlockState(Guid Id, string Identifier, Vector3 Position, Vector3 PreviousPosition, float VelocityY, JsonElement? Data);
public sealed record FallingBlockSaveState(FallingBlockState[] Active, Cell[] Pending)
{
    public int Version { get; init; } = 1;
    public static FallingBlockSaveState Empty => new([], []);
}

/// <summary>20 TPS event-driven block gravity. It does not scan resident chunks on each tick.</summary>
public sealed class FallingBlockSystem
{
    public const float GravityPerTick = .04f;
    public const float DragPerTick = .98f;
    public const float MaximumFallSpeed = 3.92f;
    public const int MaximumActiveBlocks = 4096;
    public const int MaximumPendingCells = 1_000_000;
    public const int MaximumCoordinate = 1_000_000;
    readonly World world;
    readonly Func<Cell, bool> available;
    readonly List<FallingBlock> active = [];
    readonly ReadOnlyCollection<FallingBlock> activeView;
    readonly Queue<Cell> pending = [];
    readonly HashSet<Cell> scheduled = [];
    int nextActive;
    bool ticking;
    public IReadOnlyList<FallingBlock> Active => activeView;
    public int PendingCount => pending.Count;
    public int MaxActiveUpdatesPerTick { get; set; } = 256;
    public int MaxScheduledChecksPerTick { get; set; } = 512;
    public int LastActiveUpdates { get; private set; }
    public int LastScheduledChecks { get; private set; }
    /// <summary>Hosts can delay settlement while a player overlaps the target cell.</summary>
    public Func<Cell, bool>? CanLand { get; set; }

    public FallingBlockSystem(World world, Func<Cell, bool>? availability = null)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        available = availability ?? world.IsCellAvailable;
        activeView = active.AsReadOnly();
    }

    public IReadOnlyCollection<ChunkCoord> PinnedChunks
    {
        get
        {
            if (active.Count == 0) return Array.Empty<ChunkCoord>();
            var result = new HashSet<ChunkCoord>();
            foreach (var block in active)
            {
                Cell cell = PositionCell(block.Position);
                result.Add(ChunkCoord.FromWorld(cell));
                if (cell.TryOffset(new(0, 1, 0), out var above)) result.Add(ChunkCoord.FromWorld(above));
                int belowY = Math.Max(0, (int)Math.Floor(block.Position.Y - MaximumFallSpeed));
                result.Add(ChunkCoord.FromWorld(new(cell.X, belowY, cell.Z)));
            }
            return result.ToArray();
        }
    }

    public void Schedule(Cell cell)
    {
        if (!InCoordinateRange(cell) || scheduled.Count == MaximumPendingCells || !IsGravityCell(cell) || !scheduled.Add(cell)) return;
        pending.Enqueue(cell);
    }
    public void OnCellChanged(Cell cell)
    {
        Schedule(cell);
        if (cell.TryOffset(new(0, 1, 0), out var above)) Schedule(above);
    }
    public void OnChunkLoaded(ChunkCoord coord)
    {
        if (!coord.IsValid) throw new ArgumentOutOfRangeException(nameof(coord));
        var origin = coord.Origin;
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
            Schedule(origin + new Cell(x, y, z));
    }

    bool IsGravityCell(Cell cell)
    {
        var block = world.Get(cell);
        return block != Block.Air && world.Registries.Blocks.GetInfo(block).AffectedByGravity &&
            world.Registries.Blocks.TryGetBlock(world.GetPersistentId(cell), out var known) && known == block;
    }

    public void Tick()
    {
        if (ticking) throw new InvalidOperationException("Falling-block simulation is already running.");
        if (MaxActiveUpdatesPerTick <= 0 || MaxScheduledChecksPerTick <= 0)
            throw new InvalidOperationException("Falling-block work budgets must be positive.");
        ticking = true;
        try
        {
            LastScheduledChecks = LastActiveUpdates = 0;
            int checks = Math.Min(pending.Count, MaxScheduledChecksPerTick);
            for (int i = 0; i < checks; i++)
            {
                Cell cell = pending.Dequeue(); scheduled.Remove(cell);
                LastScheduledChecks++;
                if (TryStart(cell)) Schedule(cell);
            }
            int updates = Math.Min(active.Count, MaxActiveUpdatesPerTick);
            for (int i = 0; i < updates && active.Count != 0; i++)
            {
                if (nextActive >= active.Count) nextActive = 0;
                FallingBlock block = active[nextActive];
                LastActiveUpdates++;
                if (!Advance(block)) nextActive++;
            }
        }
        finally { ticking = false; }
    }

    // True means the source still needs a retry after capacity/availability changes.
    bool TryStart(Cell cell)
    {
        if (!IsGravityCell(cell) || cell.Y <= 0 || !InCoordinateRange(cell)) return false;
        if (active.Count >= MaximumActiveBlocks || !available(cell)) return true;
        if (!cell.TryOffset(new(0, -1, 0), out var below) || !available(below)) return true;
        if (!Passable(world.Get(below))) return false;
        var position = new Vector3(cell.X + .5f, cell.Y, cell.Z + .5f);
        if (active.Any(other => SameColumn(other.Position, position) && Math.Abs(other.Position.Y - position.Y) < .9999f)) return true;
        var id = world.GetPersistentId(cell);
        Block runtime = world.Get(cell);
        JsonElement? data = world.GetBlockData(cell)?.Clone();
        var block = new FallingBlock(Guid.NewGuid(), id, runtime, position, position, 0, data);
        // Publish the actor before Set's change notifications: a listener that saves
        // during notification sees exactly one representation of the falling block.
        active.Add(block);
        try
        {
            if (!world.DetachFallingBlock(cell, runtime)) { active.Remove(block); return true; }
        }
        catch { active.Remove(block); throw; }
        if (cell.TryOffset(new(0, 1, 0), out var above)) Schedule(above);
        return false;
    }

    // Returns true after removing a settled actor; remaining actors stay in the list.
    bool Advance(FallingBlock block)
    {
        block.PreviousPosition = block.Position;
        if (!world.Registries.Blocks.TryGetBlock(block.PersistentId, out var runtime)) return false;
        block.Block = runtime;
        Cell current = PositionCell(block.Position);
        if (!available(current)) return false;
        float velocity = Math.Max(-MaximumFallSpeed, block.VelocityY - GravityPerTick);
        float target = block.Position.Y + velocity;

        float actorSupport = float.NegativeInfinity;
        foreach (var other in active)
            if (!ReferenceEquals(other, block) && SameColumn(other.Position, block.Position) &&
                other.Position.Y + 1 <= block.Position.Y + .0001f && other.Position.Y + 1 > target)
                actorSupport = Math.Max(actorSupport, other.Position.Y + 1);
        if (actorSupport > target)
        {
            block.Position = block.Position with { Y = actorSupport };
            block.VelocityY = 0;
            return false;
        }

        int lastY = Math.Max(-1, (int)Math.Floor(target));
        for (int y = (int)Math.Floor(block.Position.Y); y >= lastY; y--)
        {
            var cell = new Cell(current.X, y, current.Z);
            if (y >= 0 && !available(cell))
            {
                block.Position = block.Position with { Y = Math.Max(block.Position.Y, y + 1) };
                block.VelocityY = 0;
                return false;
            }
            if (y < 0 || !Passable(world.Get(cell)))
            {
                float landingY = y + 1;
                block.Position = block.Position with { Y = Math.Max(block.Position.Y, landingY) };
                block.VelocityY = 0;
                // Normally the first cell above the support is empty. A simultaneous
                // placement can occupy it; search a bounded distance rather than overwrite.
                for (int offset = 0; offset < 8; offset++)
                {
                    var landing = new Cell(current.X, y + 1 + offset, current.Z);
                    if (!available(landing)) return false;
                    if (!Passable(world.Get(landing))) continue;
                    block.Position = block.Position with { Y = landing.Y };
                    if (CanLand is not null && !CanLand(landing)) return false;
                    var existing = world.Get(landing);
                    if (existing is Block.Wire or Block.Lever)
                    {
                        if (!world.Mine(landing) || world.Get(landing) != Block.Air) return false;
                    }
                    int index = active.IndexOf(block);
                    active.RemoveAt(index);
                    try
                    {
                        if (!world.PlaceFallingBlock(landing, block.Block, block.PersistentId, block.Data))
                        { active.Insert(index, block); return false; }
                    }
                    catch { active.Insert(index, block); throw; }
                    Schedule(landing);
                    if (landing.TryOffset(new(0, 1, 0), out var above)) Schedule(above);
                    return true;
                }
                return false;
            }
        }
        block.Position = block.Position with { Y = Math.Max(0, target) };
        block.VelocityY = velocity * DragPerTick;
        return false;
    }

    public FallingBlockSaveState Capture() => new(active.Select(block => new FallingBlockState(
        block.Id, block.PersistentId.ToString(), block.Position, block.PreviousPosition, block.VelocityY, block.Data?.Clone())).ToArray(), pending.ToArray());

    public static void ValidateState(FallingBlockSaveState? state)
    {
        if (state is null) return;
        if (state.Version != 1 || state.Active is null || state.Pending is null || state.Active.Length > MaximumActiveBlocks ||
            state.Pending.Length > MaximumPendingCells)
            throw new InvalidDataException("Invalid falling-block save state.");
        var identities = new HashSet<Guid>();
        foreach (var block in state.Active)
        {
            if (block is null || block.Id == Guid.Empty || !identities.Add(block.Id) || !ValidPosition(block.Position) ||
                !ValidPosition(block.PreviousPosition) || block.Position.X != block.PreviousPosition.X || block.Position.Z != block.PreviousPosition.Z || !float.IsFinite(block.VelocityY) || block.VelocityY > 0 ||
                block.VelocityY < -MaximumFallSpeed || block.Data?.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Invalid falling block.");
            try
            {
                var identifier = new ContentId(block.Identifier);
                if (identifier == LegacyBlockIds.GetPersistentId((int)Block.Air)) throw new InvalidDataException("Air cannot be an active falling block.");
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid falling-block content identifier.", error); }
        }
        foreach (var column in state.Active.GroupBy(block => ((int)MathF.Floor(block.Position.X), (int)MathF.Floor(block.Position.Z))))
        {
            float previous = float.NegativeInfinity;
            foreach (var block in column.OrderBy(block => block.Position.Y))
            {
                if (block.Position.Y - previous < .9999f) throw new InvalidDataException("Overlapping falling blocks.");
                previous = block.Position.Y;
            }
        }
        if (state.Pending.Any(cell => !InCoordinateRange(cell)) || state.Pending.Distinct().Count() != state.Pending.Length)
            throw new InvalidDataException("Invalid scheduled falling-block cells.");
    }

    public void Restore(FallingBlockSaveState? state)
    {
        if (ticking) throw new InvalidOperationException("Cannot replace falling-block state during its tick.");
        ValidateState(state);
        var restored = state?.Active.Select(value =>
        {
            var id = new ContentId(value.Identifier);
            var runtime = world.Registries.Blocks.TryGetBlock(id, out var known) ? known : Block.Stone;
            return new FallingBlock(value.Id, id, runtime, value.Position, value.PreviousPosition, value.VelocityY, value.Data?.Clone());
        }).ToArray() ?? [];
        active.Clear(); active.AddRange(restored);
        pending.Clear(); scheduled.Clear(); nextActive = 0;
        if (state is not null)
            foreach (var cell in state.Pending) { pending.Enqueue(cell); scheduled.Add(cell); }
    }

    static bool Passable(Block block) => block is Block.Air or Block.Wire or Block.Lever;
    static bool SameColumn(Vector3 first, Vector3 second) => (int)MathF.Floor(first.X) == (int)MathF.Floor(second.X) &&
        (int)MathF.Floor(first.Z) == (int)MathF.Floor(second.Z);
    static Cell PositionCell(Vector3 value) => new((int)MathF.Floor(value.X), (int)MathF.Floor(value.Y), (int)MathF.Floor(value.Z));
    static bool InCoordinateRange(Cell cell) => cell.X is >= -MaximumCoordinate and <= MaximumCoordinate &&
        cell.Z is >= -MaximumCoordinate and <= MaximumCoordinate && cell.Y is >= 0 and <= MaximumCoordinate;
    static bool ValidPosition(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        value.X >= -MaximumCoordinate && value.X < MaximumCoordinate + 1f && value.Z >= -MaximumCoordinate && value.Z < MaximumCoordinate + 1f &&
        value.Y is >= 0 and <= MaximumCoordinate &&
        Math.Abs(value.X - MathF.Floor(value.X) - .5f) < .001f && Math.Abs(value.Z - MathF.Floor(value.Z) - .5f) < .001f;
}

public sealed partial class World
{
    internal bool DetachFallingBlock(Cell cell, Block expected)
    {
        if (currentEdit is not null || replayingHistory || Get(cell) != expected) return false;
        Set(cell, Block.Air);
        return Get(cell) == Block.Air;
    }

    /// <summary>Commit settlement and opaque data before notifying extension listeners.</summary>
    internal bool PlaceFallingBlock(Cell cell, Block block, ContentId id, JsonElement? data)
    {
        if (currentEdit is not null || replayingHistory || Get(cell) != Block.Air ||
            !Registries.Blocks.TryGetBlock(id, out var registered) || registered != block) return false;
        JsonElement? cloned = data?.Clone();
        Chunks.Set(cell, block);
        if (cloned.HasValue) blockPayloads[cell] = new(id, cloned);
        Circuits.EnqueueBlockChange(cell, Block.Air, block);
        Events.Publish(new BlockChangedEvent(this, cell, Block.Air, block));
        return true;
    }
}
