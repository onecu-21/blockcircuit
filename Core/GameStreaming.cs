using BlockCircuit;

sealed partial class Game
{
    WorldStreamer? streamer;
    World? streamedWorld;
    bool starterCircuitPending;
    Exception? reportedStreamingError;
    void EnsureStreaming()
    {
        if ((!HasPlayableWorld&&!preparingWorld)||NetworkClientActive||NetworkConnecting||!world.IsStreaming) return;
        if (!ReferenceEquals(streamedWorld, world) || streamer is null || !ReferenceEquals(world.ActiveStreamer, streamer))
        {
            streamer?.Dispose();
            streamer = new WorldStreamer(world, savePath, new StreamingOptions { MaxResidentColumns = 512 });
            streamedWorld = world;
            world.FallingBlocks.CanLand = cell => !CollisionSystem.OverlapsPlayer(cell, feet, player.BodyHeight)&&(multiplayerHost is null||!multiplayerHost.AnyPlayerOverlaps(cell));
        }
        streamer.Update(feet, Math.Clamp((int)Math.Ceiling(settings.DrawDistance / 16), 1, 8), yaw, pitch,multiplayerHost?.InterestPositions);
        if (streamer.LastError is not null && !ReferenceEquals(reportedStreamingError, streamer.LastError))
        {
            reportedStreamingError=streamer.LastError;
            Notice("Could not load or save nearby terrain. See game.log.");
            GameLog.Write(logPath,"stream-error",streamer.LastError.ToString());
        }
        if (starterCircuitPending && world.IsCellAvailable(new(20, 10, 20)))
        {
            world.BuildStarterCircuit(); starterCircuitPending = false;
        }
    }
    void StopStreaming(bool flush)
    {
        if (streamer is null) return;
        try { if (flush) streamer.FlushSaves(feet, yaw, pitch); }
        finally { streamer.Dispose(); streamer = null; streamedWorld = null; }
    }
}
