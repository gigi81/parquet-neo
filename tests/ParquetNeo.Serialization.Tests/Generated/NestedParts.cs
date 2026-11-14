using ParquetNeo.DataModel;

namespace ParquetNeo.Serialization.Tests.Generated;

public enum Tier
{
    Basic,
    Premium,
}

/// <summary>An object inside a row: a column that can be null, one that cannot, a list, and a property that is only written.</summary>
public sealed class Place
{
    public string Street { get; set; } = "";

    public string? City { get; set; }

    public int Zip { get; set; }

    public List<string>? Notes { get; set; }

    public int Doubled => Zip * 2;
}

/// <summary>A struct inside a row.</summary>
public struct Point
{
    public int X { get; set; }

    public int? Y { get; set; }
}

/// <summary>Three levels down: an object in a list of an object in a row.</summary>
public sealed class Line
{
    public string Sku { get; set; } = "";

    [ParquetDecimal(12, 3)]
    public decimal Price { get; set; }

    public Tier Tier { get; set; }

    public Place? Ship { get; set; }
}

/// <summary>A collection class of its own, which is created first and then added to.</summary>
public sealed class Names : List<string>
{
}

/// <summary>A type that refers to nothing else, to be a row of its own.</summary>
public sealed class Counter
{
    public string Name { get; set; } = "";

    public Dictionary<string, int> Counts { get; set; } = new();
}

/// <summary>A small row with an object that cannot be null, for reading files that have a null there.</summary>
public abstract class HomeBase
{
    public int Id { get; set; }

    public Place Home { get; set; } = new();
}

[ParquetSerializable]
public sealed class HomeSink : HomeBase
{
}

public sealed class HomeTwin : HomeBase
{
}
