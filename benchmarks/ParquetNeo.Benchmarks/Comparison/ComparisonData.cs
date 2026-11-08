namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>
/// The dataset of the comparison benchmarks: 1M rows of a required long, a nullable string with 5,000 distinct values
/// (dictionary friendly, one null in ten), a nullable double (one null in three), a required int in a small range and a
/// required bool. Held column-wise, as the low-level APIs want it, with the nullable columns also as plain nullable arrays
/// (for libraries that take those) and the rows as objects (for the serializers).
/// </summary>
public sealed class ComparisonData
{
    public const int Rows = 1_000_000;

    public long[] Ids { get; } = new long[Rows];

    public int[] Quantities { get; } = new int[Rows];

    public bool[] Shipped { get; } = new bool[Rows];

    /// <summary>The names that are not null, in order, and a definition level (1 present, 0 null) for every row.</summary>
    public string[] CustomerValues { get; }

    public int[] CustomerLevels { get; } = new int[Rows];

    public string?[] Customers { get; } = new string?[Rows];

    public double[] AmountValues { get; }

    public int[] AmountLevels { get; } = new int[Rows];

    public double?[] Amounts { get; } = new double?[Rows];

    public Order[] Orders { get; } = new Order[Rows];

    public ComparisonData()
    {
        var customers = new List<string>();
        var amounts = new List<double>();
        for (int i = 0; i < Rows; i++)
        {
            Ids[i] = i;
            Quantities[i] = 1 + i % 20;
            Shipped[i] = i % 4 != 0;
            if (i % 10 != 0)
            {
                string name = "customer-" + (i % 5000);
                Customers[i] = name;
                customers.Add(name);
                CustomerLevels[i] = 1;
            }

            if (i % 3 != 0)
            {
                double amount = i * 0.25;
                Amounts[i] = amount;
                amounts.Add(amount);
                AmountLevels[i] = 1;
            }

            Orders[i] = new Order { Id = Ids[i], Customer = Customers[i], Amount = Amounts[i], Quantity = Quantities[i], Shipped = Shipped[i] };
        }

        CustomerValues = [.. customers];
        AmountValues = [.. amounts];
    }
}
