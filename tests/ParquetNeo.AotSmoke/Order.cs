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
