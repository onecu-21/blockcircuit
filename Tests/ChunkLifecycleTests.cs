namespace BlockCircuit;

/// <summary>Resident chunk lifetime, persistence dirtiness, and coordinate-limit acceptance checks.</summary>
static class ChunkLifecycleTests
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
        TestCoordinates(Check);
        TestLifecycle(Check);
        TestDirtiness(Check);
        Console.WriteLine($"Chunk lifecycle tests passed: {passed}");
    }

    static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (OverflowException) { return true; }
    }

    static void TestCoordinates(Action<bool, string> check)
    {
        check(new ChunkCoord(ChunkCoord.Minimum, 0, ChunkCoord.Maximum).IsValid &&
              !new ChunkCoord(ChunkCoord.Minimum - 1, 0, 0).IsValid &&
              !new ChunkCoord(0, ChunkCoord.Maximum + 1, 0).IsValid &&
              !new ChunkCoord(0, 0, int.MaxValue).IsValid,
            "chunk coordinate validity covers exactly the origins representable by signed 32-bit cells");
        bool roundtrips = true;
        foreach (int value in new[] { int.MinValue, int.MinValue + 15, -17, -16, -1, 0, 15, 16, int.MaxValue - 15, int.MaxValue })
        {
            var world = new Cell(value, value, value);
            var coord = ChunkCoord.FromWorld(world);
            var local = ChunkCoord.ToLocal(world);
            roundtrips &= coord.IsValid && coord.Origin + local == world &&
                local.X is >= 0 and < 16 && local.Y is >= 0 and < 16 && local.Z is >= 0 and < 16;
        }
        check(roundtrips, "negative and extreme cell coordinates roundtrip through floor division without integer wrap");
        check(Rejected(() => _ = new ChunkCoord(ChunkCoord.Maximum + 1, 0, 0).Origin) &&
              Rejected(() => _ = new ChunkCoord(0, ChunkCoord.Minimum - 1, 0).Origin) &&
              Rejected(() => _ = new Chunk(new ChunkCoord(int.MaxValue, 0, 0))),
            "unrepresentable chunk origins and chunk construction fail explicitly");
    }

    static void TestLifecycle(Action<bool, string> check)
    {
        var life = new ChunkLifecycle();
        check(life.State == ChunkState.Unloaded && Rejected(() => life.BeginMeshing()) &&
              Rejected(() => life.BeginUnloading()),
            "a new unloaded lifecycle rejects meshing and unloading before generation");
        long firstLoad = life.BeginLoading();
        check(life.State == ChunkState.Loading && Rejected(() => life.BeginLoading()) &&
              !life.CompleteLoading(firstLoad + 1),
            "loading has a single active operation and rejects foreign completion tokens");
        check(life.Cancel(firstLoad) && life.State == ChunkState.Unloaded && !life.CompleteLoading(firstLoad),
            "cancelling a load restores Unloaded and prevents stale publication");
        long failedLoad = life.BeginLoading();
        var error = new IOException("injected load failure");
        check(failedLoad > firstLoad && life.Fail(failedLoad, error) &&
              life.State == ChunkState.Unloaded && ReferenceEquals(life.LastError, error),
            "failed loads retain diagnostics and return to a retryable Unloaded state");
        long loaded = life.BeginLoading();
        check(life.LastError is null && life.CompleteLoading(loaded) && life.State == ChunkState.Generated &&
              !life.CompleteLoading(loaded),
            "a fresh successful load clears failure diagnostics and publishes Generated only once");
        long staleMesh = life.BeginMeshing();
        check(life.State == ChunkState.Meshing && !life.CompleteLoading(staleMesh),
            "an operation token cannot complete the wrong lifecycle transition");
        life.Invalidate();
        check(life.State == ChunkState.Generated && !life.CompleteMeshing(staleMesh) && !life.Cancel(staleMesh),
            "an edit invalidates a pending mesh so its old result cannot be published");
        long ready = life.BeginMeshing();
        check(life.CompleteMeshing(ready) && life.State == ChunkState.Ready,
            "a current mesh completion makes a generated chunk Ready");
        long reupload = life.BeginMeshing();
        check(life.Fail(reupload, error) && life.State == ChunkState.Generated,
            "a Ready chunk can reupload in a fresh renderer and retry failed meshing from Generated");
        ready = life.BeginMeshing();
        life.CompleteMeshing(ready);
        long unload = life.BeginUnloading();
        check(life.Cancel(unload) && life.State == ChunkState.Ready && !life.CompleteUnloading(unload),
            "cancelled unloading restores the previous Ready state and rejects stale completion");
        life.Invalidate();
        long generatedUnload = life.BeginUnloading();
        check(life.Fail(generatedUnload, error) && life.State == ChunkState.Generated,
            "failed unloading of an unmeshed chunk restores Generated for retry");
        unload = life.BeginUnloading();
        check(life.CompleteUnloading(unload) && life.State == ChunkState.Unloaded &&
              !life.Fail(unload, error) && !life.Cancel(0),
            "successful unloading releases readiness and completed or zero tokens cannot mutate state");
    }

    static void TestDirtiness(Action<bool, string> check)
    {
        var manager = new ChunkManager();
        Cell cell = new(-17, -1, 16);
        var coord = ChunkCoord.FromWorld(cell);
        manager.Set(cell, Block.Air);
        manager.MarkContentDirty(cell);
        check(manager.DirtyCoordinates.Count == 0 && manager.GetContentRevision(coord) == 0,
            "no-op writes and metadata touches on untouched air allocate no dirty chunk records");
        manager.Set(cell, Block.Stone);
        var chunk = manager.Chunks[coord];
        long content = manager.GetContentRevision(coord);
        check(chunk.IsDirty && chunk.ContentRevision == content && content > 0 &&
              manager.DirtyCoordinates.SequenceEqual([coord]) && chunk.Lifecycle.State == ChunkState.Generated,
            "a changed negative-coordinate chunk receives a persistence epoch and Generated resident lifetime");
        manager.MarkAllSaved();
        long mesh = chunk.Revision;
        manager.Set(cell, Block.Stone);
        check(!chunk.IsDirty && manager.DirtyCoordinates.Count == 0 &&
              chunk.ContentRevision == content && chunk.Revision == mesh,
            "no-op block writes preserve both saved content state and mesh revisions");
        long meshToken = chunk.Lifecycle.BeginMeshing();
        chunk.Lifecycle.CompleteMeshing(meshToken);
        manager.MarkDirty(cell);
        check(chunk.Revision > mesh && chunk.ContentRevision == content && !chunk.IsDirty &&
              manager.DirtyCoordinates.Count == 0 && chunk.Lifecycle.State == ChunkState.Generated,
            "mesh invalidation schedules rebuilding without dirtying persisted block content");
        mesh = chunk.Revision;
        manager.MarkContentDirty(cell);
        long payloadContent = manager.GetContentRevision(coord);
        check(chunk.IsDirty && payloadContent > content && chunk.ContentRevision == payloadContent &&
              chunk.Revision == mesh && manager.Get(cell) == Block.Stone,
            "block data or lever-only changes dirty content without modifying block IDs or mesh revisions");
        check(!manager.MarkSaved(coord, content) && chunk.IsDirty &&
              manager.MarkSaved(coord, payloadContent) && !chunk.IsDirty && manager.DirtyCoordinates.Count == 0,
            "save acknowledgements clear dirtiness only for the exact captured content revision");

        var boundary = new ChunkManager();
        var left = new ChunkCoord(0, 0, 0);
        var right = new ChunkCoord(1, 0, 0);
        boundary.Set(new(8, 8, 8), Block.Stone);
        boundary.Set(new(16, 8, 8), Block.Stone);
        boundary.MarkAllSaved();
        var leftChunk = boundary.Chunks[left];
        var rightChunk = boundary.Chunks[right];
        long leftMesh = leftChunk.Revision, rightMesh = rightChunk.Revision;
        long rightContent = rightChunk.ContentRevision;
        boundary.Set(new(15, 8, 8), Block.Dirt);
        check(boundary.DirtyCoordinates.SequenceEqual([left]) && leftChunk.IsDirty && !rightChunk.IsDirty &&
              rightChunk.ContentRevision == rightContent && leftChunk.Revision > leftMesh && rightChunk.Revision > rightMesh,
            "a chunk-boundary edit rebuilds both meshes but saves only its changed owner content");
        var dirtySnapshot = boundary.DirtyCoordinates;
        if (dirtySnapshot is ChunkCoord[] copied) copied[0] = right;
        check(boundary.DirtyCoordinates.SequenceEqual([left]),
            "dirty-coordinate snapshots cannot mutate the manager's pending save set");

        var recreated = new ChunkManager();
        Cell origin = new(0, 0, 0);
        var zero = new ChunkCoord(0, 0, 0);
        recreated.Set(origin, Block.Stone);
        recreated.MarkAllSaved();
        var retired = recreated.Chunks[zero];
        long pendingMesh = retired.Lifecycle.BeginMeshing();
        check(recreated.Remove(zero) && retired.Lifecycle.State == ChunkState.Unloaded &&
              !retired.Lifecycle.CompleteMeshing(pendingMesh) && recreated.DirtyCoordinates.SequenceEqual([zero]),
            "removing a chunk retires pending mesh work and retains a dirty deletion tombstone");
        long deletedVersion = recreated.GetContentRevision(zero);
        recreated.Set(origin, Block.Dirt);
        long newVersion = recreated.GetContentRevision(zero);
        long recreatedMesh = recreated.Chunks[zero].Lifecycle.BeginMeshing();
        check(recreatedMesh != pendingMesh && !recreated.Chunks[zero].Lifecycle.CompleteMeshing(pendingMesh) &&
              recreated.Chunks[zero].Lifecycle.CompleteMeshing(recreatedMesh),
            "mesh operation tokens cannot publish work into a different chunk incarnation at the same coordinate");
        check(newVersion > deletedVersion && !recreated.MarkSaved(zero, deletedVersion) &&
              recreated.Chunks[zero].IsDirty && recreated.Get(origin) == Block.Dirt,
            "a stale deletion save cannot clear a newer recreated chunk");
        check(recreated.MarkSaved(zero, newVersion) && recreated.DirtyCoordinates.Count == 0,
            "saving the recreated revision clears only its matching dirty content");
        var last = recreated.Chunks[zero];
        recreated.Set(origin, Block.Air);
        long emptyVersion = recreated.GetContentRevision(zero);
        check(recreated.Chunks.Count == 0 && last.Lifecycle.State == ChunkState.Unloaded &&
              emptyVersion > newVersion && recreated.DirtyCoordinates.SequenceEqual([zero]) &&
              !recreated.MarkSaved(zero, newVersion),
            "removing the final block releases storage while preserving a newer deletion tombstone");
        check(recreated.MarkSaved(zero, emptyVersion) && recreated.DirtyCoordinates.Count == 0 &&
              recreated.GetContentRevision(zero) == 0 && !recreated.MarkSaved(zero, emptyVersion),
            "a committed deletion can be acknowledged once without keeping an empty resident chunk");
        check(!recreated.Remove(zero) && Rejected(() => recreated.GetContentRevision(new ChunkCoord(int.MaxValue, 0, 0))) &&
              Rejected(() => recreated.Remove(new ChunkCoord(0, int.MinValue, 0))),
            "absent removals are no-ops and persistence APIs reject unrepresentable chunk coordinates");
    }
}
