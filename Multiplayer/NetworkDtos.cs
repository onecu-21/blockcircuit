using System.Numerics;

namespace BlockCircuit;

public sealed record NetPose(Vector3 Position,float Yaw,float Pitch,bool Flying=false);
public sealed record RemotePlayerState(Guid Id,string Name,Vector3 Position,float Yaw,float Pitch,bool Flying=false);
public enum NetCommandKind { Place, Mine, Toggle, Click, Shift, Drag, Assign, Craft, Drop }

/// <summary>A player intent, never a client-provided replacement inventory or world state.</summary>
public sealed record NetCommand(NetCommandKind Kind)
{
    public long Sequence { get; init; }
    public Cell Cell { get; init; }
    public int Slot { get; init; }
    public ItemSlot? Source { get; init; }
    public ItemSlot? Target { get; init; }
    public ItemSlot[]? Targets { get; init; }
    public bool Right { get; init; }
    public string? Item { get; init; }
    public int Size { get; init; } = 2;
    public bool ToStorage { get; init; }
    public bool Repeat { get; init; }
    public bool Whole { get; init; }
    public Cell? Bench { get; init; }
}

internal sealed record NetHello(int Version,string Fingerprint,string Name,string Password,Guid Identity);
internal sealed record NetWelcome(int Version,Guid PlayerId,int Seed,bool Streaming,NetPose Spawn,PlayerInventoryState Inventory);
internal sealed record NetChunk(ChunkCoord Coord,ChunkFileData? Data);
internal sealed record NetPower(Cell Cell,int Level);
internal sealed record NetState(long Tick,long AckSequence,bool TerrainReady,bool CorrectPosition,RemotePlayerState[] Players,PlayerInventoryState Inventory,
    FallingBlockSaveState Falling,DroppedItemData[] Drops,NetPower[] Power,ColumnCoord[] Available);
public sealed record NetCommandResult(long Sequence,bool Accepted,string Reason);
internal sealed record NetFailure(string Reason);
