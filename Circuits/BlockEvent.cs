namespace BlockCircuit;

public enum BlockEventKind { BlockChanged, SwitchChanged }

/// <summary>Queued changes retain both block IDs, including edits applied before the next circuit update.</summary>
public readonly record struct BlockEvent(Cell Position, Block Original, Block Current, BlockEventKind Kind);
