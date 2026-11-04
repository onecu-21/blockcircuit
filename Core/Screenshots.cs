using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    void TryScreenshot()
    {
        try
        {
            string directory = Path.Combine(dataDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}-{Guid.NewGuid():N}.png");
            Raylib.TakeScreenshot(path);
            if (!File.Exists(path)) throw new IOException("Screenshot file could not be written.");
            Notice("Screenshot saved in BlockCircuit/screenshots.");
            GameLog.Write(logPath, "screenshot", path);
        }
        catch (Exception error)
        {
            Notice("Screenshot failed: " + error.Message);
            GameLog.Write(logPath, "screenshot-error", error.Message);
        }
    }
}
