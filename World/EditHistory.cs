namespace BlockCircuit;

internal readonly record struct EditCellState(Block Block, bool SwitchOn);
internal readonly record struct EditCellChange(Cell Cell, EditCellState Before, EditCellState After);

/// <summary>One user action stores only touched cells and inventory deltas, including support side effects.</summary>
internal sealed class EditTransaction
{
    public Dictionary<Cell, EditCellChange> Cells { get; } = [];
    public Dictionary<Block, int> InventoryDeltas { get; } = [];
    public bool HasChanges => Cells.Count > 0 || InventoryDeltas.Count > 0;

    public void RecordCell(Cell cell, EditCellState before, EditCellState after)
    {
        if (Cells.TryGetValue(cell, out var existing)) before = existing.Before;
        if (before == after) Cells.Remove(cell);
        else Cells[cell] = new(cell, before, after);
    }

    public void RecordInventory(Block block, int delta)
    {
        int total = InventoryDeltas.GetValueOrDefault(block) + delta;
        if (total == 0) InventoryDeltas.Remove(block);
        else InventoryDeltas[block] = total;
    }
}

internal sealed class EditHistory(int capacity)
{
    private readonly List<EditTransaction> undo = [];
    private readonly List<EditTransaction> redo = [];
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public EditTransaction? UndoEntry => undo.Count == 0 ? null : undo[^1];
    public EditTransaction? RedoEntry => redo.Count == 0 ? null : redo[^1];

    public void Record(EditTransaction edit)
    {
        if (!edit.HasChanges) return;
        undo.Add(edit);
        if (undo.Count > capacity) undo.RemoveAt(0);
        redo.Clear();
    }

    public void FinishUndo()
    {
        redo.Add(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    public void FinishRedo()
    {
        undo.Add(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
