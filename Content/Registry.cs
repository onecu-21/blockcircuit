namespace BlockCircuit;

/// <summary>Registration is single-threaded; frozen registries support concurrent reads.</summary>
public class Registry<T> where T : notnull
{
    readonly List<T> values = [];
    readonly List<ContentId> identifiers = [];
    readonly Dictionary<ContentId, int> runtimeIds = [];
    public int Count => values.Count;
    public bool IsFrozen { get; private set; }
    public int Register(ContentId id, T value)
    {
        _ = id.ToString();
        ArgumentNullException.ThrowIfNull(value);
        if (IsFrozen) throw new InvalidOperationException("Registry is frozen.");
        if (runtimeIds.ContainsKey(id)) throw new ArgumentException("Duplicate content identifier.", nameof(id));
        int runtimeId = values.Count;
        runtimeIds.Add(id, runtimeId); values.Add(value); identifiers.Add(id);
        return runtimeId;
    }
    public T Get(int runtimeId) => (uint)runtimeId < values.Count ? values[runtimeId] : throw new ArgumentOutOfRangeException(nameof(runtimeId));
    public T Get(ContentId id) => Get(GetRuntimeId(id));
    public ContentId GetId(int runtimeId) => (uint)runtimeId < identifiers.Count ? identifiers[runtimeId] : throw new ArgumentOutOfRangeException(nameof(runtimeId));
    public int GetRuntimeId(ContentId id) => runtimeIds.TryGetValue(id, out int value) ? value : throw new KeyNotFoundException($"Missing content: {id}");
    public bool TryGetRuntimeId(ContentId id, out int runtimeId) => runtimeIds.TryGetValue(id, out runtimeId);
    public void Freeze() => IsFrozen = true;
}

public sealed record ItemDefinition(string Name, int MaxStack = 64);
public sealed record EntityTypeDefinition(string Name);
public sealed class ItemRegistry : Registry<ItemDefinition> { }
public sealed class EntityTypeRegistry : Registry<EntityTypeDefinition> { }

public sealed class BlockRegistry
{
    readonly Registry<BlockInfo> registry = new();
    public int Count => registry.Count;
    public bool IsFrozen => registry.IsFrozen;
    public Block Register(ContentId id, BlockInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if ((uint)info.TextureBlock >= BlockCatalog.Count) throw new ArgumentException("Texture fallback must be a built-in block.", nameof(info));
        Block block = (Block)registry.Count;
        registry.Register(id, info with { Id = block });
        return block;
    }
    public bool IsValid(Block block) => (uint)block < Count;
    public BlockInfo GetInfo(Block block) => registry.Get((int)block);
    public ContentId GetId(Block block) => registry.GetId((int)block);
    public Block GetBlock(ContentId id) => (Block)registry.GetRuntimeId(id);
    public bool TryGetBlock(ContentId id, out Block block)
    {
        bool found = registry.TryGetRuntimeId(id, out int runtimeId);
        block = (Block)runtimeId; return found;
    }
    public IEnumerable<BlockInfo> Placeable => Enumerable.Range(0, Count).Select(i => registry.Get(i)).Where(b => b.Id is not (Block.Air or Block.Bedrock));
    public int[] CreateInventory()
    {
        var counts = new int[Count];
        foreach (var info in Placeable) counts[(int)info.Id] = 128;
        return counts;
    }
    public void Freeze() => registry.Freeze();
}

public sealed class GameRegistries
{
    public BlockRegistry Blocks { get; } = new();
    public ItemRegistry Items { get; } = new();
    public EntityTypeRegistry EntityTypes { get; } = new();
    public static GameRegistries Default { get; } = Create();
    GameRegistries() { }
    public static GameRegistries Create(Action<GameRegistries>? configure = null)
    {
        var result = new GameRegistries();
        foreach (var info in BlockCatalog.Builtins)
        {
            var id = LegacyBlockIds.GetPersistentId((int)info.Id);
            result.Blocks.Register(id, info with { TextureBlock = info.Id, AffectedByGravity = info.Id is Block.Sand or Block.RedSand or Block.Gravel });
            if (info.Id is not (Block.Air or Block.Bedrock)) result.Items.Register(id, new(info.Name));
        }
        configure?.Invoke(result);
        result.Blocks.Freeze(); result.Items.Freeze(); result.EntityTypes.Freeze();
        return result;
    }
}
