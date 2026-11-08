using ParquetNeo.Serialization;

namespace ParquetNeo.Tests.Serialization;

/// <summary>
/// A row type shaped like the nested sample dataset (Core/Nested/NestedDataset.cs and tests/fixtures/nested_dataset.py): the
/// serializer's schema for it equals the dataset's, and its rows are the dataset's, so the existing references apply.
/// </summary>
public sealed class Document
{
    [ParquetColumn("id")]
    public long Id { get; set; }

    [ParquetColumn("scores")]
    public List<int?>? Scores { get; set; }

    [ParquetColumn("matrix")]
    public List<List<int?>?>? Matrix { get; set; }

    [ParquetColumn("person")]
    public Person? Person { get; set; }

    [ParquetColumn("events")]
    public List<Event?>? Events { get; set; }

    [ParquetColumn("attributes")]
    public Dictionary<string, int?>? Attributes { get; set; }

    [ParquetColumn("labels")]
    public List<string> Labels { get; set; } = [];

    public static List<Document> Rows(int count = 200) => Enumerable.Range(0, count).Select(Row).ToList();

    public static Document Row(int i) => new()
    {
        Id = i,
        Scores = (i % 6) switch
        {
            0 => null,
            1 => [],
            2 => [i],
            3 => [i, null, i + 1],
            4 => [null],
            _ => Enumerable.Range(0, i % 5 + 3).Select(n => (int?)n).ToList(),
        },
        Matrix = (i % 5) switch
        {
            0 => null,
            1 => [],
            2 => [[1, 2], [3]],
            3 => [null, [i]],
            _ => [[i], [], [i + 1, i + 2, i + 3]],
        },
        Person = (i % 4) switch
        {
            0 => null,
            1 => new Person { Name = $"n{i}", Address = new Address { City = $"c{i % 3}", Zip = i } },
            2 => new Person { Name = null, Address = null },
            _ => new Person { Name = $"n{i}", Address = new Address { City = null, Zip = null } },
        },
        Events = (i % 5) switch
        {
            0 => null,
            1 => [],
            2 => [new Event { Kind = $"k{i % 3}", Count = i }],
            3 => [new Event { Kind = null, Count = i }, new Event { Kind = "x", Count = null }, null],
            _ => Enumerable.Range(0, i % 3 + 1).Select(n => (Event?)new Event { Kind = $"k{i % 2}", Count = i + n }).ToList(),
        },
        Attributes = (i % 4) switch
        {
            0 => null,
            1 => [],
            2 => new() { ["a"] = i, ["b"] = null },
            _ => new() { [$"k{i % 5}"] = i, ["z"] = 1 },
        },
        Labels = (i % 3) switch
        {
            0 => [],
            1 => [$"l{i}"],
            _ => ["a", "b", "c"],
        },
    };
}

public sealed class Person
{
    [ParquetColumn("name")]
    public string? Name { get; set; }

    [ParquetColumn("address")]
    public Address? Address { get; set; }
}

public sealed class Address
{
    [ParquetColumn("city")]
    public string? City { get; set; }

    [ParquetColumn("zip")]
    public int? Zip { get; set; }
}

public sealed class Event
{
    [ParquetColumn("kind")]
    public string? Kind { get; set; }

    [ParquetColumn("count")]
    public int? Count { get; set; }
}
