namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>One row of the comparison dataset: the five columns every library writes and reads.</summary>
public sealed class Order
{
    public long Id { get; set; }

    public string? Customer { get; set; }

    public double? Amount { get; set; }

    public int Quantity { get; set; }

    public bool Shipped { get; set; }
}
