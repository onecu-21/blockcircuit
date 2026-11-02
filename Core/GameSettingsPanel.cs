using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    bool settingsOpen;
    string settingsStatus = "Changes apply immediately. Save keeps them for next time.";
    bool settingsSaveFailed;

    void OpenSettings()
    {
        settingsOpen = true;
        mining = null;
        mineTime = 0;
        settingsStatus = "Changes apply immediately. Save keeps them for next time.";
        settingsSaveFailed = false;
        Raylib.EnableCursor();
    }

    void CloseSettings()
    {
        settingsOpen = false;
        if (paused || inventoryOpen) Raylib.EnableCursor();
        else Raylib.DisableCursor();
    }

    void DrawSettingsPanel()
    {
        int screenWidth = Raylib.GetScreenWidth(), screenHeight = Raylib.GetScreenHeight();
        int width = Math.Min(760, screenWidth - 48), height = Math.Min(590, screenHeight - 40);
        int x = (screenWidth - width) / 2, y = (screenHeight - height) / 2;
        int left = x + 28, contentWidth = width - 56;
        Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(9, 16, 24, 214));
        Raylib.DrawRectangle(x, y, width, height, ink);
        Raylib.DrawRectangle(x, y, 4, height, accent);
        Raylib.DrawText("SETTINGS", left, y + 25, 30, Color.White);
        DrawTextFit("Make movement comfortable and balance detail with performance.", left, y + 65, 15, contentWidth, muted);

        Raylib.DrawText("FRAME RATE LIMIT", left, y + 107, 17, Color.White);
        DrawTextFit("Lower limits use less power. Higher limits can make movement smoother.", left, y + 132, 14, contentWidth, muted);
        int[] limits = [30, 60, 120, 144, 0];
        int gap = 8, buttonWidth = (contentWidth - gap * 4) / 5;
        for (int i = 0; i < limits.Length; i++)
        {
            int limit = limits[i];
            string label = limit == 0 ? "UNLIMITED" : $"{limit} FPS";
            var rectangle = new Rectangle(left + i * (buttonWidth + gap), y + 155, buttonWidth, 34);
            if (Button(rectangle, label, settings.TargetFps == limit))
            {
                settings.TargetFps = limit;
                Raylib.SetTargetFPS(settings.TargetFps);
                SettingsChanged();
            }
        }

        float fov = SettingsSlider(left, y + 209, contentWidth, "FIELD OF VIEW",
            "See more around you with a wider view.", settings.FieldOfView, 60, 100, 1, $"{settings.FieldOfView:0} degrees");
        if (fov != settings.FieldOfView) { settings.FieldOfView = fov; SettingsChanged(); }

        float sensitivity = SettingsSlider(left, y + 292, contentWidth, "MOUSE SENSITIVITY",
            "Choose how quickly the camera turns when you move the mouse.", settings.MouseSensitivity, .001f, .006f, .0001f,
            $"{settings.MouseSensitivity / .0025f:0.00}x");
        if (sensitivity != settings.MouseSensitivity) { settings.MouseSensitivity = sensitivity; SettingsChanged(); }

        float distance = SettingsSlider(left, y + 375, contentWidth, "VIEW DISTANCE",
            "See farther across the world. A shorter distance can improve performance.", settings.DrawDistance, 32, 128, 8,
            $"{settings.DrawDistance:0} blocks");
        if (distance != settings.DrawDistance)
        {
            settings.DrawDistance = distance;
            renderer.DrawDistance = settings.DrawDistance;
            SettingsChanged();
        }

        DrawTextFit(settingsStatus, left, y + 462, 14, contentWidth,
            settingsSaveFailed ? new Color(255, 151, 137, 255) : accent);
        DrawTextFit("Your hotbar and selected slot are saved with these settings.", left, y + 489, 13, contentWidth, muted);
        int footer = y + height - 56;
        if (Button(new Rectangle(left, footer, 115, 36), "SAVE", true))
        {
            try
            {
                settings.CapturePalette(palette);
                settings.Save(settingsPath);
                settingsStatus = "Settings and hotbar saved.";
                settingsSaveFailed = false;
                GameLog.Write(logPath, "SETTINGS_SAVE", "Settings and hotbar saved.");
                Notice(settingsStatus);
            }
            catch (Exception error)
            {
                settingsStatus = "Could not save settings. Check that your save folder is writable.";
                settingsSaveFailed = true;
                GameLog.Write(logPath, "SETTINGS_SAVE_FAILED", $"{error.GetType().Name}: {error.Message}");
                Notice("Settings could not be saved.");
            }
        }
        if (Button(new Rectangle(left + 127, footer, 174, 36), "RESET DEFAULTS"))
        {
            settings.ResetToDefaults();
            settings.ApplyToPalette(palette);
            Raylib.SetTargetFPS(settings.TargetFps);
            renderer.DrawDistance = settings.DrawDistance;
            settingsStatus = "Default settings and hotbar restored. Save to keep them.";
            settingsSaveFailed = false;
        }
        if (Button(new Rectangle(x + width - 143, footer, 115, 36), "CLOSE")) CloseSettings();
    }

    void SettingsChanged()
    {
        settingsStatus = "Changes applied. Save keeps them for next time.";
        settingsSaveFailed = false;
    }

    float SettingsSlider(int x, int y, int width, string label, string description,
        float value, float minimum, float maximum, float step, string valueText)
    {
        DrawTextFit(label, x, y, 17, width - 145, Color.White);
        int valueWidth = Raylib.MeasureText(valueText, 16);
        Raylib.DrawText(valueText, x + width - valueWidth, y + 1, 16, accent);
        DrawTextFit(description, x, y + 24, 14, width, muted);
        var hit = new Rectangle(x, y + 41, width, 22);
        var mouse = Raylib.GetMousePosition();
        bool hovered = Raylib.CheckCollisionPointRec(mouse, hit);
        if (hovered && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            float fraction = Math.Clamp((mouse.X - x) / width, 0f, 1f);
            value = Math.Clamp(minimum + MathF.Round(fraction * (maximum - minimum) / step) * step, minimum, maximum);
        }
        float position = (value - minimum) / (maximum - minimum);
        int trackY = y + 49;
        Raylib.DrawRectangle(x, trackY, width, 6, tileColor);
        Raylib.DrawRectangle(x, trackY, (int)(width * position), 6, accent);
        int knobX = x + (int)(width * position);
        Raylib.DrawCircle(knobX, trackY + 3, hovered ? 8 : 7, accent);
        Raylib.DrawCircle(knobX, trackY + 3, 3, ink);
        return value;
    }
}
