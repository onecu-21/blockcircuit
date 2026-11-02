namespace BlockCircuit;

/// <summary>
/// Keeps circuit adjacency and connected components between updates. Only components touched
/// by topology/switch events are evaluated; ordinary terrain edits do not recalculate power.
/// Call Rebuild after directly changing the public Switches collection or replacing world data.
/// </summary>
public sealed class CircuitSystem
{
    private sealed class Node(Block block)
    {
        public Block Block { get; } = block;
        public HashSet<Cell> Adjacent { get; } = [];
    }

    private readonly World world;
    private readonly Queue<BlockEvent> events = new();
    private readonly Dictionary<Cell, Node> nodes = [];
    private readonly Dictionary<int, CircuitNetwork> networks = [];
    private readonly Dictionary<Cell, int> membership = [];
    private int nextNetworkId;

    public int NetworkCount => networks.Count;
    public int CircuitCellCount => nodes.Count;
    public IEnumerable<Cell> Cells => nodes.Keys;
    public int PendingEventCount => events.Count;
    /// <summary>Distinct affected component cells inspected by the most recent update.</summary>
    public int LastVisitedCells { get; private set; }
    public int LastUpdatedNetworks { get; private set; }
    public long TotalNetworkUpdates { get; private set; }

    internal CircuitSystem(World world) => this.world = world;

    public int? GetNetworkId(Cell cell) => membership.TryGetValue(cell, out int id) ? id : null;

    internal void EnqueueBlockChange(Cell cell, Block original, Block current)
        => events.Enqueue(new(cell, original, current, BlockEventKind.BlockChanged));

    internal void EnqueueSwitchChange(Cell cell)
        => events.Enqueue(new(cell, Block.Lever, Block.Lever, BlockEventKind.SwitchChanged));

    public void ProcessPending()
    {
        LastVisitedCells = 0;
        LastUpdatedNetworks = 0;
        if (events.Count == 0) return;

        var topology = new HashSet<Cell>();
        var switches = new HashSet<Cell>();
        while (events.TryDequeue(out var change))
        {
            if (change.Kind == BlockEventKind.SwitchChanged) switches.Add(change.Position);
            else if (IsCircuit(change.Original) || IsCircuit(change.Current)) topology.Add(change.Position);
        }
        if (topology.Count == 0 && switches.Count == 0) return;

        var affected = new HashSet<Cell>();
        var updates = new HashSet<int>();
        if (topology.Count > 0)
        {
            // Capture complete old components before changing any edges. This handles a batch
            // containing several additions/removals, including component splits and merges.
            var oldIds = new HashSet<int>();
            foreach (var cell in topology)
            {
                if (membership.TryGetValue(cell, out int own)) oldIds.Add(own);
                foreach (var offset in World.Neighbors)
                    if (membership.TryGetValue(cell + offset, out int neighbor)) oldIds.Add(neighbor);
            }
            var previousNetworks = oldIds.Select(id => networks[id]).ToArray();
            foreach (var network in previousNetworks)
            {
                networks.Remove(network.Id);
                foreach (var cell in network.Members)
                {
                    membership.Remove(cell);
                    affected.Add(cell);
                }
            }

            // Remove old adjacency first, then add final block states and rebuild local edges.
            // Intermediate event states never leak into the final graph.
            foreach (var cell in topology)
            {
                affected.Add(cell);
                if (nodes.Remove(cell, out var previous))
                    foreach (var neighbor in previous.Adjacent)
                        if (nodes.TryGetValue(neighbor, out var adjacent)) adjacent.Adjacent.Remove(cell);
            }
            foreach (var cell in topology)
            {
                var block = world.Get(cell);
                if (IsCircuit(block)) nodes.Add(cell, new(block));
            }
            foreach (var cell in topology)
                if (nodes.TryGetValue(cell, out var node)) Connect(cell, node);

            foreach (var cell in affected)
                if (nodes.ContainsKey(cell) && !membership.ContainsKey(cell)) updates.Add(BuildNetwork(cell).Id);
            // A fully removed component also represents one completed network update.
            LastUpdatedNetworks += previousNetworks.Count(network => !network.Members.Any(nodes.ContainsKey));
        }

        foreach (var cell in switches)
            if (membership.TryGetValue(cell, out int id)) updates.Add(id);

        var nextPower = new Dictionary<Cell, int>();
        foreach (int id in updates)
        {
            var network = networks[id];
            affected.UnionWith(network.Members);
            Evaluate(network, nextPower);
        }
        LastVisitedCells = affected.Count;
        LastUpdatedNetworks += updates.Count;
        TotalNetworkUpdates += LastUpdatedNetworks;
        ApplyPower(affected, nextPower);
    }

    /// <summary>Explicit fallback for generation, loading and direct external state edits.</summary>
    public void Rebuild()
    {
        events.Clear();
        events.TrimExcess();
        nodes.Clear();
        networks.Clear();
        membership.Clear();
        for (int y = 0; y < World.Height; y++) for (int z = 0; z < World.Size; z++) for (int x = 0; x < World.Size; x++)
        {
            var cell = new Cell(x, y, z);
            var block = world.Get(cell);
            if (IsCircuit(block)) nodes.Add(cell, new(block));
        }
        foreach (var (cell, node) in nodes) Connect(cell, node);
        foreach (var cell in nodes.Keys)
            if (!membership.ContainsKey(cell)) BuildNetwork(cell);

        var nextPower = new Dictionary<Cell, int>();
        foreach (var network in networks.Values) Evaluate(network, nextPower);
        var affected = new HashSet<Cell>(world.Power.Keys);
        affected.UnionWith(nodes.Keys);
        ApplyPower(affected, nextPower);
        LastVisitedCells = nodes.Count;
        LastUpdatedNetworks = networks.Count;
        TotalNetworkUpdates += LastUpdatedNetworks;
    }

    private void Connect(Cell cell, Node node)
    {
        foreach (var offset in World.Neighbors)
        {
            var neighbor = cell + offset;
            if (!nodes.TryGetValue(neighbor, out var adjacent)) continue;
            node.Adjacent.Add(neighbor);
            adjacent.Adjacent.Add(cell);
        }
    }

    private CircuitNetwork BuildNetwork(Cell seed)
    {
        var network = new CircuitNetwork(++nextNetworkId);
        networks.Add(network.Id, network);
        var queue = new Queue<Cell>();
        membership.Add(seed, network.Id);
        queue.Enqueue(seed);
        while (queue.TryDequeue(out var cell))
        {
            network.Members.Add(cell);
            foreach (var neighbor in nodes[cell].Adjacent)
            {
                if (!membership.TryAdd(neighbor, network.Id)) continue;
                queue.Enqueue(neighbor);
            }
        }
        return network;
    }

    private void Evaluate(CircuitNetwork network, Dictionary<Cell, int> nextPower)
    {
        var queue = new Queue<Cell>();
        foreach (var cell in network.Members)
            if (nodes[cell].Block == Block.Lever && world.Switches.Contains(cell))
            {
                nextPower[cell] = 15;
                queue.Enqueue(cell);
            }
        while (queue.TryDequeue(out var cell))
        {
            int signal = nextPower[cell] - 1;
            if (signal <= 0) continue;
            foreach (var neighbor in nodes[cell].Adjacent)
            {
                var block = nodes[neighbor].Block;
                if (block is not (Block.Wire or Block.Lamp) || nextPower.GetValueOrDefault(neighbor) >= signal) continue;
                nextPower[neighbor] = signal;
                if (block == Block.Wire) queue.Enqueue(neighbor);
            }
        }
    }

    private void ApplyPower(IEnumerable<Cell> affected, Dictionary<Cell, int> nextPower)
    {
        foreach (var cell in affected)
        {
            bool existed = world.Power.TryGetValue(cell, out int previous);
            int current = nextPower.GetValueOrDefault(cell);
            if (previous == current)
            {
                // Rebuild also normalizes callers' direct edits to the public power dictionary.
                if (current == 0 && existed) world.Power.Remove(cell);
                continue;
            }
            if (current == 0) world.Power.Remove(cell);
            else world.Power[cell] = current;
            world.Chunks.MarkDirty(cell);
        }
    }

    private static bool IsCircuit(Block block) => block is Block.Wire or Block.Lever or Block.Lamp;
}
