using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockCircuit;

/// <summary>Validated player preferences, stored independently from a saved world.</summary>
public sealed class GameSettings
{
    const int CurrentVersion = 1;
    int targetFps = 60;
    float fieldOfView = 72f, mouseSensitivity = .0025f, drawDistance = 64f;
    Block[] hotbar = DefaultHotbar();
    string[]? hotbarIds;
    Block[]? lastAppliedHotbar;
    BlockRegistry? lastAppliedRegistry;
    int selectedSlot;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Version { get; set; } = CurrentVersion;
    public int TargetFps
    {
        get => targetFps;
        set => targetFps = value is 0 or 30 or 60 or 120 or 144 or 240 ? value : 60;
    }
    public float FieldOfView
    {
        get => fieldOfView;
        set => fieldOfView = float.IsFinite(value) ? Math.Clamp(value, 60f, 100f) : 72f;
    }
    public float MouseSensitivity
    {
        get => mouseSensitivity;
        set => mouseSensitivity = float.IsFinite(value) ? Math.Clamp(value, .001f, .006f) : .0025f;
    }
    public float DrawDistance
    {
        get => drawDistance;
        set => drawDistance = float.IsFinite(value) ? Math.Clamp(value, 32f, 128f) : 64f;
    }
    public Block[] Hotbar
    {
        get => hotbar;
        set => hotbar = ValidatedHotbar(value);
    }
    /// <summary>Persistent content identities; legacy Hotbar remains a safe built-in fallback.</summary>
    public string[]? HotbarIds
    {
        get => hotbarIds?.ToArray();
        set => hotbarIds = ValidatedHotbarIds(value);
    }
    public int SelectedSlot
    {
        get => selectedSlot;
        set => selectedSlot = Math.Clamp(value, 0, BlockPaletteModel.HotbarCount - 1);
    }
    [JsonIgnore]
    public string? LoadWarning { get; private set; }

    public static GameSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new GameSettings();
            var result = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path), JsonOptions);
            if (result is null || result.Version != CurrentVersion)
                return DefaultsWithWarning("Unsupported settings file; default settings loaded.");
            result.hotbar = ValidatedHotbar(result.hotbar);
            return result;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return DefaultsWithWarning("Settings could not be read; default settings loaded.");
        }
    }

    public void Save(string path)
    {
        Version = CurrentVersion;
        hotbar = ValidatedHotbar(hotbar);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryPath, fullPath, true);
            LoadWarning = null;
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void ApplyToPalette(BlockPaletteModel palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        hotbar = ValidatedHotbar(hotbar);
        var resolved = hotbar.ToArray();
        if (hotbarIds is not null)
            for (int slot = 0; slot < resolved.Length; slot++)
                if (palette.Blocks.TryGetBlock(new ContentId(hotbarIds[slot]), out var block) && block is not (Block.Air or Block.Bedrock))
                    resolved[slot] = block;
        Array.Copy(resolved, palette.Hotbar, palette.Hotbar.Length);
        lastAppliedHotbar = resolved;
        lastAppliedRegistry = palette.Blocks;
        palette.SelectedSlot = selectedSlot;
    }

    public void CapturePalette(BlockPaletteModel palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var fallback = ValidatedHotbar(palette.Hotbar);
        var ids = new string[BlockPaletteModel.HotbarCount];
        for (int slot = 0; slot < ids.Length; slot++)
        {
            // Preserve an unavailable mod selection if the player has left its shown fallback unchanged.
            bool keepMissing = hotbarIds is not null && lastAppliedHotbar is not null &&
                ReferenceEquals(lastAppliedRegistry, palette.Blocks) && palette.Hotbar[slot] == lastAppliedHotbar[slot] &&
                !palette.Blocks.TryGetBlock(new ContentId(hotbarIds[slot]), out _);
            if (keepMissing) ids[slot] = hotbarIds![slot];
            else if (palette.Blocks.IsValid(palette.Hotbar[slot]) && palette.Hotbar[slot] is not (Block.Air or Block.Bedrock))
                ids[slot] = palette.Blocks.GetId(palette.Hotbar[slot]).ToString();
            else ids[slot] = GameRegistries.Default.Blocks.GetId(fallback[slot]).ToString();
        }
        hotbar = fallback;
        hotbarIds = ids;
        lastAppliedHotbar = palette.Hotbar.ToArray();
        lastAppliedRegistry = palette.Blocks;
        SelectedSlot = palette.SelectedSlot;
    }

    public void ResetToDefaults()
    {
        Version = CurrentVersion;
        TargetFps = 60;
        FieldOfView = 72f;
        MouseSensitivity = .0025f;
        DrawDistance = 64f;
        hotbar = DefaultHotbar();
        hotbarIds = null;
        lastAppliedHotbar = null;
        lastAppliedRegistry = null;
        SelectedSlot = 0;
        LoadWarning = null;
    }

    static Block[] DefaultHotbar() =>
        [Block.Grass, Block.Dirt, Block.Stone, Block.Coal, Block.Iron, Block.Wire, Block.Lever, Block.Lamp, Block.Glass];

    static Block[] ValidatedHotbar(Block[]? value)
    {
        var valid = DefaultHotbar();
        // Existing settings have eight slots. Keep their selections and append the new default ninth slot.
        if (value is null || value.Length is not (8 or BlockPaletteModel.HotbarCount)) return valid;
        for (int slot = 0; slot < value.Length; slot++)
            if (BlockCatalog.IsValid(value[slot]) && value[slot] is not (Block.Air or Block.Bedrock))
                valid[slot] = value[slot];
        return valid;
    }


    static string[]? ValidatedHotbarIds(string[]? value)
    {
        if (value is null || value.Length is not (8 or BlockPaletteModel.HotbarCount)) return null;
        var defaults = DefaultHotbar();
        var valid = new string[BlockPaletteModel.HotbarCount];
        for (int slot = 0; slot < valid.Length; slot++)
        {
            valid[slot] = GameRegistries.Default.Blocks.GetId(defaults[slot]).ToString();
            if (slot >= value.Length) continue;
            try { valid[slot] = new ContentId(value[slot]).ToString(); }
            catch (ArgumentException) { }
        }
        return valid;
    }

    static GameSettings DefaultsWithWarning(string message) => new() { LoadWarning = message };
}
