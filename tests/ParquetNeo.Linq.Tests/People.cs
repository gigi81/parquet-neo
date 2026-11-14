using ParquetNeo.DataModel;

namespace ParquetNeo.Linq.Tests;

public enum Level
{
    Junior,
    Senior,
}

public sealed class Address
{
    public string Street { get; set; } = "";

    public int Number { get; set; }
}

/// <summary>A row of the datasets the tests query.</summary>
public sealed class Person
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public int Age { get; set; }

    public string City { get; set; } = "";

    public double? Score { get; set; }

    public DateOnly Born { get; set; }

    public bool Active { get; set; }

    public Level Level { get; set; }

    [ParquetColumn("home_zip")]
    public int Zip { get; set; }

    public Address Home { get; set; } = new();

    public List<string> Tags { get; set; } = [];

    /// <summary>Written, but not read: it is worked out from the others.</summary>
    public string Label => $"{Name}-{Id}";
}
