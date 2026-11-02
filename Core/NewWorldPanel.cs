using System.Globalization;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    bool newWorldOpen, classicTerrain;
    string newWorldSeed = "731";
    string newWorldStatus = "";

    void OpenNewWorld()
    {
        newWorldOpen = true;
        newWorldSeed = world.Seed.ToString(CultureInfo.InvariantCulture);
        classicTerrain = false;
        newWorldStatus = "";
        settingsOpen = inventoryOpen = false;
        ResetSimulation();
        Raylib.EnableCursor();
    }

    void CloseNewWorld()
    {
        newWorldOpen = false;
        ResetSimulation();
        if (paused) Raylib.EnableCursor(); else Raylib.DisableCursor();
    }

    void DrawNewWorldPanel()
    {
        int width = Math.Min(720, Raylib.GetScreenWidth() - 48), height = 430;
        int x = (Raylib.GetScreenWidth() - width) / 2, y = (Raylib.GetScreenHeight() - height) / 2;
        int left = x + 28, available = width - 56;
        Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), new Color(9,16,24,214));
        Raylib.DrawRectangle(x, y, width, height, ink);
        Raylib.DrawRectangle(x, y, 4, height, accent);
        Raylib.DrawText("CREATE A NEW WORLD", left, y + 25, 28, Color.White);
        DrawTextFit("Your current world is saved to a separate backup before creating a new one.", left, y + 69, 14, available, muted);
        Raylib.DrawText("WORLD SEED", left, y + 112, 16, Color.White);
        var input = new Rectangle(left, y + 139, available - 150, 46);
        Raylib.DrawRectangleRec(input, tileColor);
        int key;
        while ((key = Raylib.GetCharPressed()) != 0)
        {
            if (newWorldSeed.Length < 11 && (key >= '0' && key <= '9' || key == '-' && newWorldSeed.Length == 0))
                newWorldSeed += (char)key;
        }
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.A)) newWorldSeed = "";
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && newWorldSeed.Length > 0) newWorldSeed = newWorldSeed[..^1];
        Raylib.DrawText(newWorldSeed + ((int)(Raylib.GetTime()*2)%2==0 ? "|" : ""), left + 12, y + 150, 23, accent);
        if (Button(new Rectangle(x + width - 162, y + 139, 134, 46), "RANDOM"))
            newWorldSeed = Random.Shared.Next().ToString(CultureInfo.InvariantCulture);
        DrawTextFit("Type a number. Ctrl+A clears it. The same seed creates the same terrain.", left, y + 196, 13, available, muted);
        int half = (available - 12) / 2;
        if (Button(new Rectangle(left, y + 235, half, 40), "NATURAL: hills and caves", !classicTerrain)) classicTerrain = false;
        if (Button(new Rectangle(left + half + 12, y + 235, half, 40), "CLASSIC: original terrain", classicTerrain)) classicTerrain = true;
        DrawTextFit(newWorldStatus.Length == 0 ? "New terrain changes the landscape and replenishes the starting inventory." : newWorldStatus,
            left, y + 299, 14, available, newWorldStatus.Length == 0 ? muted : new Color(255,151,137,255));
        if (Button(new Rectangle(left, y + 359, 210, 40), "CREATE NEW WORLD", true))
        {
            if (!int.TryParse(newWorldSeed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                newWorldStatus = "Enter a whole number from -2147483648 to 2147483647.";
            else
            {
                try
                {
                    var created = WorldSession.Create(world, feet, yaw, pitch, seed, classicTerrain, savePath);
                    world = created.World;
                    feet = world.Spawn(); previousFeet = feet; yaw = MathF.PI; pitch = -.18f;
                    player.VerticalVelocity = 0; player.Flying = false; paused = false;
                    CloseNewWorld();
                    Notice($"New world created. Seed {seed}. Previous world backed up.");
                    GameLog.Write(logPath, "world-created", $"Seed {seed}; backup {created.BackupPath}");
                }
                catch (Exception error)
                {
                    newWorldStatus = "Could not create the world. Your current session is still open.";
                    GameLog.Write(logPath, "world-create-error", error.Message);
                }
            }
        }
        if (Button(new Rectangle(x + width - 148, y + 359, 120, 40), "CANCEL")) CloseNewWorld();
    }
}
