namespace BlockCircuit;

/// <summary>A best-effort local diagnostic log. Callers pass event summaries, not save contents.</summary>
public static class GameLog
{
    static readonly object Gate = new();

    public static void Write(string path, string eventName, string message)
    {
        lock (Gate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.AppendAllText(fullPath,
                    $"{DateTimeOffset.UtcNow:O} [{OneLine(eventName, 64)}] {OneLine(message, 2048)}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A missing or unwritable log must never interrupt play or hide the original error.
            }
        }
    }

    static string OneLine(string? text, int maxLength)
    {
        text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
