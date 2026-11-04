namespace BlockCircuit;

/// <summary>Ordered subscriptions, snapshot dispatch and bounded subscriber error reporting.</summary>
public sealed class EventHub
{
    readonly Dictionary<Type, List<Registration>> handlers = [];
    readonly List<Exception> errors = [];
    public IReadOnlyList<Exception> Errors => errors.AsReadOnly();
    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!handlers.TryGetValue(typeof(T), out var list)) handlers.Add(typeof(T), list = []);
        var registration = new Registration(handler);
        list.Add(registration);
        return new Subscription(() => list.Remove(registration));
    }
    public void Publish<T>(T value)
    {
        if (!handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
        foreach (var handler in list.ToArray())
            try { ((Action<T>)handler.Handler)(value); }
            catch (Exception error) { if (errors.Count == 128) errors.RemoveAt(0); errors.Add(error); }
    }
    sealed class Registration(Delegate handler) { public Delegate Handler { get; } = handler; }
    sealed class Subscription(Action unsubscribe) : IDisposable
    {
        Action? release = unsubscribe;
        public void Dispose() { release?.Invoke(); release = null; }
    }
}
public readonly record struct BlockChangedEvent(World World, Cell Cell, Block Previous, Block Current);
public readonly record struct BlockPlacedEvent(World World, Cell Cell, Block Block);
public readonly record struct BlockBrokenEvent(World World, Cell Cell, Block Block);
public readonly record struct GameTickEvent(World World, long Tick);
