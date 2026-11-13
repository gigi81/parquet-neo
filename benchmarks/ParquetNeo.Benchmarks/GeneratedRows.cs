using ParquetNeo.DataModel;

namespace ParquetNeo.Benchmarks;

/// <summary>The kinds a flat row can be of.</summary>
public enum RowKind
{
    Small,
    Medium,
    Large,
}

/// <summary>A flat row: required and nullable numbers, text, a date, a flag and an enum.</summary>
public abstract class FlatRowShape
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public double Score { get; set; }

    public int? Count { get; set; }

    public DateTime Created { get; set; }

    public bool Active { get; set; }

    public RowKind Kind { get; set; }

    public decimal? Amount { get; set; }
}

/// <summary>The flat row with a mapping generated at build time.</summary>
[ParquetSerializable]
public sealed class GeneratedFlatRow : FlatRowShape
{
}

/// <summary>The flat row left to reflection.</summary>
public sealed class ReflectedFlatRow : FlatRowShape
{
}

public sealed class RowCustomer
{
    public string Name { get; set; } = "";

    public int Age { get; set; }

    public bool Vip { get; set; }
}

public sealed class RowAddress
{
    public string City { get; set; } = "";

    public int Zip { get; set; }
}

public sealed class RowLine
{
    public string Sku { get; set; } = "";

    public int Quantity { get; set; }

    public double Price { get; set; }
}

/// <summary>A row with objects, lists, an array and a dictionary inside.</summary>
public abstract class NestedRowShape
{
    public long Id { get; set; }

    public RowCustomer? Customer { get; set; }

    public RowAddress Address { get; set; } = new();

    public double? Amount { get; set; }

    public List<string>? Tags { get; set; }

    public double[] Scores { get; set; } = [];

    public List<RowLine> Lines { get; set; } = [];

    public Dictionary<string, int> Counts { get; set; } = new();
}

/// <summary>The nested row with a mapping generated at build time.</summary>
[ParquetSerializable]
public sealed class GeneratedNestedRow : NestedRowShape
{
}

/// <summary>The nested row left to reflection.</summary>
public sealed class ReflectedNestedRow : NestedRowShape
{
}

/// <summary>Rows for the benchmarks of generated mappings.</summary>
public static class GeneratedRowData
{
    public static TRow FillFlat<TRow>(TRow row, int i)
        where TRow : FlatRowShape
    {
        row.Id = i;
        row.Name = i % 10 == 0 ? null : "name-" + (i % 1000);
        row.Score = i * 0.5;
        row.Count = i % 7 == 0 ? null : i;
        row.Created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i);
        row.Active = i % 3 == 0;
        row.Kind = (RowKind)(i % 3);
        row.Amount = i % 5 == 0 ? null : i * 0.01m;
        return row;
    }

    public static TRow FillNested<TRow>(TRow row, int i)
        where TRow : NestedRowShape
    {
        row.Id = i;
        row.Customer = i % 10 == 0 ? null : new RowCustomer { Name = "customer-" + (i % 5000), Age = 20 + (i % 50), Vip = i % 7 == 0 };
        row.Address = new RowAddress { City = "city-" + (i % 300), Zip = 10000 + (i % 9000) };
        row.Amount = i % 3 == 0 ? null : i * 0.25;
        row.Tags = i % 4 == 0 ? null : Enumerable.Range(0, i % 4).Select(t => "tag-" + (t + (i % 20))).ToList();
        row.Scores = Enumerable.Range(0, i % 5).Select(t => (double)(i + t)).ToArray();
        row.Lines = Enumerable.Range(0, i % 4).Select(t => new RowLine { Sku = "sku-" + ((i + t) % 1000), Quantity = 1 + t, Price = 9.99 + t }).ToList();
        row.Counts = i % 3 == 0 ? new() : new() { ["a"] = i % 10, ["b"] = i % 100 };
        return row;
    }
}
