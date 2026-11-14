using ParquetNeo.DataModel;

namespace ParquetNeo.AotSmoke;

/// <summary>The states an order goes through.</summary>
public enum OrderStatus
{
    New,
    Shipped,
    Returned,
}

/// <summary>A flat row of a few kinds of column: a required value, a nullable string, an enum and a nullable value type; its mapping is written by the source generator.</summary>
[ParquetSerializable]
public sealed class Order
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public double Score { get; set; }

    public OrderStatus Status { get; set; }

    public int? Quantity { get; set; }
}

/// <summary>An object inside a row.</summary>
public sealed class Address
{
    public string Street { get; set; } = "";

    public int? Zip { get; set; }
}

/// <summary>A row with objects, collections and dictionaries inside it: all of them need generated code to work with Native AOT.</summary>
[ParquetSerializable]
public sealed class Customer
{
    public int Id { get; set; }

    public Address Home { get; set; } = new();

    public Address? Work { get; set; }

    public List<Order> Orders { get; set; } = [];

    public string[] Tags { get; set; } = [];

    public HashSet<int> Lucky { get; set; } = [];

    public Dictionary<string, List<int>> Scores { get; set; } = new();

    public Dictionary<string, Address?> Places { get; set; } = new();
}

/// <summary>A record inside a row: it is created by its constructor.</summary>
public sealed record InvoiceLine(string Sku, int Quantity);

/// <summary>A row that is a record with a record inside a list: with Native AOT the constructors are called by generated code.</summary>
[ParquetSerializable]
public sealed record Invoice(int Number, string Customer, decimal? Total, List<InvoiceLine> Lines)
{
    public string? Note { get; init; }
}
