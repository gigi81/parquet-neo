using ParquetNeo.DataModel;

namespace ParquetNeo.Tests.Serialization.Generated;

/// <summary>A record inside a row, which is created by its constructor.</summary>
public sealed record Spot(int X, int? Y);

/// <summary>A struct with no setters, which is created by its constructor.</summary>
public readonly struct Coord
{
    public Coord(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }

    public int Y { get; }
}

/// <summary>A record with objects, collections and constructor-built types inside it.</summary>
[ParquetSerializable]
public sealed record Citizen(
    int Id,
    string Name,
    DateOnly Born,
    Place? Home,
    Spot Where,
    List<Spot> Path,
    Dictionary<string, Spot?> Named,
    Coord Origin,
    Coord? Last,
    string[] Tags);

/// <summary>The same as <see cref="Citizen"/>, left to reflection.</summary>
public sealed record CitizenTwin(
    int Id,
    string Name,
    DateOnly Born,
    Place? Home,
    Spot Where,
    List<Spot> Path,
    Dictionary<string, Spot?> Named,
    Coord Origin,
    Coord? Last,
    string[] Tags);

/// <summary>Properties that are taken by a constructor, by a setter, by neither, and a constructor that none fits.</summary>
public abstract class MixedBase
{
    protected MixedBase(int id, string label)
    {
        Id = id;
        Label = label;
    }

    public int Id { get; }

    public string Label { get; }

    public string? Note { get; set; }

    public decimal? Amount { get; init; }

    public int Computed => Id * 2;
}

/// <summary>The parameters match the properties by name without regard to case; the constructor with more parameters wins.</summary>
[ParquetSerializable]
public sealed class Mixed : MixedBase
{
    public Mixed(int id)
        : base(id, "")
    {
    }

    public Mixed(string text)
        : base(0, text)
    {
    }

    public Mixed(int ID, string label)
        : base(ID, label)
    {
    }
}

/// <summary>The same as <see cref="Mixed"/>, left to reflection.</summary>
public sealed class MixedTwin : MixedBase
{
    public MixedTwin(int id)
        : base(id, "")
    {
    }

    public MixedTwin(string text)
        : base(0, text)
    {
    }

    public MixedTwin(int ID, string label)
        : base(ID, label)
    {
    }
}

/// <summary>A struct that is a row, with the values of its parameters.</summary>
[ParquetSerializable]
public readonly record struct Pair(int A, string? B);

public readonly record struct PairTwin(int A, string? B);
