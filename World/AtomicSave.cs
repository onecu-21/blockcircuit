using System.Text;
using System.Text.Json;

namespace BlockCircuit;

internal static class AtomicSave
{
    internal static void Write(string path, string text, string? backup = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                { writer.Write(text); writer.Flush(); }
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, backup);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void BackupLegacy(string path, int maximumVersion = 3)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number) || number is not (1 or 2 or 3 or 4) || number > maximumVersion) return;
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "backups");
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + $"-legacy-v{number}-{Guid.NewGuid():N}.json");
        if(number==4)
        {
            var manifest=ChunkSaveStore.ParseManifest(document.RootElement);
            string chunks=backup+".chunks";
            Directory.CreateDirectory(chunks);
            foreach(var reference in manifest.Chunks)
            {
                string source=Path.Combine(path+".chunks",reference.File);
                // A recovered primary may already reference a missing file; retain its raw metadata without blocking repair.
                if(File.Exists(source))File.Copy(source,Path.Combine(chunks,reference.File));
            }
        }
        File.Copy(path,backup);
    }
}
