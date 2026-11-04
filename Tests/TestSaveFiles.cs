namespace BlockCircuit;

/// <summary>Remove only sidecars belonging to a unique temporary test save.</summary>
internal static class TestSaveFiles
{
    internal static void Cleanup(string path)
    {
        string full = Path.GetFullPath(path);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup must stay inside the temporary directory.");
        File.Delete(full); File.Delete(full + ".tmp"); File.Delete(full + ".bak"); File.Delete(full + ".lock");
        string chunks = full + ".chunks";
        if (Directory.Exists(chunks))
        {
            if ((File.GetAttributes(chunks) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to follow a linked test chunk directory.");
            Directory.Delete(chunks, recursive: true);
        }
    }
}
