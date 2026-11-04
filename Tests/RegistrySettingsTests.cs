using System.Text.Json;
using BlockCircuit;

namespace BlockCircuit;

/// <summary>Stable hotbar identity and compatibility across registry changes.</summary>
static class RegistrySettingsTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
            passed++;
        }

        string path = Path.Combine(Path.GetTempPath(), "blockcircuit-registry-settings-" + Guid.NewGuid().ToString("N") + ".json");
        BlockInfo Custom(string name) => new(Block.Air, name, BlockCategory.Stone, 0xA0B0C0, BlockPattern.Stone);
        var alphaId = new ContentId("settings_test:alpha");
        var betaId = new ContentId("settings_test:beta");
        var first = GameRegistries.Create(registries =>
        {
            registries.Blocks.Register(alphaId, Custom("Alpha"));
            registries.Blocks.Register(betaId, Custom("Beta"));
        });
        var reordered = GameRegistries.Create(registries =>
        {
            registries.Blocks.Register(betaId, Custom("Beta"));
            registries.Blocks.Register(alphaId, Custom("Alpha"));
        });
        var defaults = new BlockPaletteModel().Hotbar.ToArray();
        try
        {
            var palette = new BlockPaletteModel { Blocks = first.Blocks, SelectedSlot = 8 };
            palette.Hotbar[0] = first.Blocks.GetBlock(alphaId);
            palette.Hotbar[8] = first.Blocks.GetBlock(betaId);
            var settings = new GameSettings();
            settings.CapturePalette(palette);
            Check(settings.HotbarIds![0] == alphaId.ToString() && settings.HotbarIds[8] == betaId.ToString(),
                "capturing a mod hotbar stores stable content identifiers instead of runtime IDs");
            palette.Hotbar[0] = Block.Stone;
            Check(settings.HotbarIds![0] == alphaId.ToString(),
                "capturing a hotbar does not retain mutable live palette storage");
            settings.Save(path);
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                Check(json.RootElement.GetProperty("Version").GetInt32() == 1 &&
                      json.RootElement.GetProperty("HotbarIds")[0].GetString() == alphaId.ToString() &&
                      settings.Hotbar[0] == defaults[0],
                    "version-one settings retain a safe legacy hotbar alongside stable mod identifiers");

            var loaded = GameSettings.Load(path);
            var secondPalette = new BlockPaletteModel { Blocks = reordered.Blocks };
            loaded.ApplyToPalette(secondPalette);
            Check(first.Blocks.GetBlock(alphaId) != reordered.Blocks.GetBlock(alphaId) &&
                  secondPalette.Hotbar[0] == reordered.Blocks.GetBlock(alphaId) &&
                  secondPalette.Hotbar[8] == reordered.Blocks.GetBlock(betaId) && secondPalette.SelectedSlot == 8,
                "reordered registries restore the same mod selections and selected slot");
            secondPalette.Hotbar[0] = Block.Dirt;
            var thirdPalette = new BlockPaletteModel { Blocks = reordered.Blocks };
            loaded.ApplyToPalette(thirdPalette);
            Check(thirdPalette.Hotbar[0] == reordered.Blocks.GetBlock(alphaId),
                "applying settings does not share mutable palette arrays between applications");

            var idArray = loaded.HotbarIds!;
            idArray[0] = betaId.ToString();
            Check(loaded.HotbarIds![0] == alphaId.ToString(),
                "reading stable hotbar identifiers does not expose mutable settings storage");
            var assigned = loaded.HotbarIds!;
            var assignedSettings = new GameSettings { HotbarIds = assigned };
            assigned[0] = betaId.ToString();
            Check(assignedSettings.HotbarIds![0] == alphaId.ToString(),
                "assigning stable hotbar identifiers copies the caller's array");

            File.WriteAllText(path, JsonSerializer.Serialize(new { HotbarIds = loaded.HotbarIds, Hotbar = defaults, Version = 1 }));
            var reversedProperties = GameSettings.Load(path);
            reversedProperties.ApplyToPalette(thirdPalette);
            Check(thirdPalette.Hotbar[0] == reordered.Blocks.GetBlock(alphaId),
                "persistent hotbar identities take priority independently of JSON property order");

            Block[] oldEight = [Block.CherryLog, Block.BlueGlass, Block.DiamondBlock, Block.RedWool,
                Block.StoneBricks, Block.Wire, Block.Lever, Block.Lamp];
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Hotbar = oldEight, SelectedSlot = 7 }));
            var legacy = GameSettings.Load(path);
            var legacyPalette = new BlockPaletteModel();
            legacy.ApplyToPalette(legacyPalette);
            Check(legacy.LoadWarning is null && legacyPalette.Hotbar.Take(8).SequenceEqual(oldEight) &&
                  legacyPalette.Hotbar[8] == Block.Glass && legacyPalette.SelectedSlot == 7,
                "legacy eight-slot version-one settings preserve selections and append clear glass");

            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, HotbarIds = loaded.HotbarIds!.Take(8).ToArray() }));
            var eightIdentifiers = GameSettings.Load(path);
            eightIdentifiers.ApplyToPalette(thirdPalette);
            Check(thirdPalette.Hotbar[0] == reordered.Blocks.GetBlock(alphaId) && thirdPalette.Hotbar[8] == Block.Glass,
                "eight-slot stable-identifier settings also migrate to a ninth clear-glass slot");

            settings.Save(path);
            var missing = GameSettings.Load(path);
            var fallbackPalette = new BlockPaletteModel();
            missing.ApplyToPalette(fallbackPalette);
            Check(fallbackPalette.Hotbar.SequenceEqual(defaults),
                "unavailable mod hotbar entries display valid built-in fallback selections");
            missing.CapturePalette(fallbackPalette);
            missing.Save(path);
            var restoredMods = GameSettings.Load(path);
            restoredMods.ApplyToPalette(thirdPalette);
            Check(restoredMods.HotbarIds![0] == alphaId.ToString() &&
                  thirdPalette.Hotbar[0] == reordered.Blocks.GetBlock(alphaId) &&
                  thirdPalette.Hotbar[8] == reordered.Blocks.GetBlock(betaId),
                "an unchanged missing-mod fallback survives resaving and restores after the mod returns");

            missing.ApplyToPalette(fallbackPalette);
            fallbackPalette.Hotbar[0] = Block.Stone;
            missing.CapturePalette(fallbackPalette);
            missing.Save(path);
            var replaced = GameSettings.Load(path);
            replaced.ApplyToPalette(thirdPalette);
            Check(thirdPalette.Hotbar[0] == Block.Stone && replaced.HotbarIds![0] ==
                  GameRegistries.Default.Blocks.GetId(Block.Stone).ToString(),
                "explicitly replacing a missing-mod fallback saves the new selection");

            fallbackPalette.Hotbar[0] = (Block)int.MaxValue;
            missing.CapturePalette(fallbackPalette);
            Check(missing.Hotbar[0] == defaults[0] && missing.HotbarIds![0] ==
                  GameRegistries.Default.Blocks.GetId(defaults[0]).ToString(),
                "an invalid custom runtime selection safely falls back without throwing");

            string[] malformed = [null!, "UPPER:wrong", "unqualified", "blockcircuit:air", "settings_test:absent",
                "blockcircuit:wire", "blockcircuit:lever", "blockcircuit:lamp", "blockcircuit:glass"];
            var sanitized = new GameSettings { HotbarIds = malformed };
            sanitized.ApplyToPalette(fallbackPalette);
            Check(fallbackPalette.Hotbar[0] == defaults[0] && fallbackPalette.Hotbar[1] == defaults[1] &&
                  fallbackPalette.Hotbar[2] == defaults[2] && fallbackPalette.Hotbar[3] == defaults[3] &&
                  fallbackPalette.Hotbar[4] == defaults[4],
                "malformed, air, and unavailable hotbar identifiers cannot create invalid selections");
            sanitized.ResetToDefaults();
            sanitized.ApplyToPalette(thirdPalette);
            Check(sanitized.HotbarIds is null && thirdPalette.Hotbar.SequenceEqual(defaults) && thirdPalette.SelectedSlot == 0,
                "resetting settings clears retained mod identifiers and restores all default slots");

            Console.WriteLine($"Registry settings tests passed: {passed}");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
