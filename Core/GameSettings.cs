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
        set => targetFps = value is 0 or 30 or 60 or 120 or 144 ? value : 60;
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
    public int SelectedSlot
    {
        get => selectedSlot;
        set => selectedSlot = Math.Clamp(value, 0, 7);
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
        Array.Copy(hotbar, palette.Hotbar, 8);
        palette.SelectedSlot = selectedSlot;
    }

    public void CapturePalette(BlockPaletteModel palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        hotbar = ValidatedHotbar(palette.Hotbar);
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
        SelectedSlot = 0;
        LoadWarning = null;
    }

    static Block[] DefaultHotbar() =>
        [Block.Grass, Block.Dirt, Block.Stone, Block.Coal, Block.Iron, Block.Wire, Block.Lever, Block.Lamp];

    static Block[] ValidatedHotbar(Block[]? value)
    {
        var valid = DefaultHotbar();
        if (value is not { Length: 8 }) return valid;
        for (int slot = 0; slot < valid.Length; slot++)
            if (BlockCatalog.IsValid(value[slot]) && value[slot] is not (Block.Air or Block.Bedrock))
                valid[slot] = value[slot];
        return valid;
    }

    static GameSettings DefaultsWithWarning(string message) => new() { LoadWarning = message };
}
