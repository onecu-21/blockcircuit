namespace BlockCircuit;

/// <summary>A connected group of adjacent circuit blocks; lamps are terminal during signal propagation.</summary>
public sealed class CircuitNetwork
{
    internal HashSet<Cell> Members { get; } = [];
    public int Id { get; }
    public int CellCount => Members.Count;
    public IEnumerable<Cell> Cells => Members;

    internal CircuitNetwork(int id) => Id = id;
}
