using System.Numerics;

namespace BlockCircuit;

/// <summary>Create and persist a new world only after saving the current session to a separate backup.</summary>
public static class WorldSession
{
    public static (World World, string BackupPath) Create(
        World current, Vector3 position, float yaw, float pitch,
        int seed, bool legacyTerrain, string savePath)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.ActiveStreamer is not null)
        {
            current.ActiveStreamer.FlushSaves(position, yaw, pitch);
            current.ActiveStreamer.Dispose();
        }
        string fullPath = Path.GetFullPath(savePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        var candidate = current.IsStreaming && !legacyTerrain ? World.CreateStreaming(seed, current.Registries)
            : new World(seed: seed, legacyTerrain: legacyTerrain, registries: current.Registries);
        if (current.IsStreaming) candidate.EnableStreaming();
        string backup = Path.Combine(directory, "backups",
            $"world-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        current.ExportSnapshot(backup, position, yaw, pitch);
        candidate.Save(fullPath, candidate.Spawn(), MathF.PI, -.18f);
        return (candidate, backup);
    }
}
