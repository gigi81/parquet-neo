using ParquetNeo.DataModel;

namespace ParquetNeo.Serialization.Tests.Generated;

/// <summary>
/// Every shape a property can have besides a single column: objects, structs, lists, arrays, sets, dictionaries and the nestings of
/// them. <see cref="NestedSink"/> gets a generated mapping and <see cref="NestedTwin"/> is left to reflection.
/// </summary>
public abstract class NestedBase
{
    public int Id { get; set; }

    public Place Home { get; set; } = new();

    public Place? Work { get; set; }

    public Point Where { get; set; }

    public Point? Spot { get; set; }

    public List<string> Tags { get; set; } = [];

    public List<string>? MaybeTags { get; set; }

    public string[] Codes { get; set; } = [];

    public List<string?> Sparse { get; set; } = [];

    public List<int?> Scores { get; set; } = [];

    [ParquetDecimal(10, 2)]
    public IList<decimal> Amounts { get; set; } = [];

    public IReadOnlyList<int> ReadOnly { get; set; } = [];

    public HashSet<string> Labels { get; set; } = [];

    public ISet<int> Numbers { get; set; } = new HashSet<int>();

    public Names Team { get; set; } = [];

    public List<Place> Previous { get; set; } = [];

    public Place?[] Many { get; set; } = [];

    public List<Line> Lines { get; set; } = [];

    public List<List<int>> Matrix { get; set; } = [];

    public Dictionary<string, int> Counts { get; set; } = new();

    public IDictionary<int, List<string>> Groups { get; set; } = new Dictionary<int, List<string>>();

    public Dictionary<string, Place> ByName { get; set; } = new();

    public Dictionary<string, int?> Optionals { get; set; } = new();

    public IReadOnlyDictionary<string, string?> Notes { get; set; } = new Dictionary<string, string?>();

    [ParquetDecimal(8, 1)]
    public Dictionary<string, decimal> Prices { get; set; } = new();

    public Dictionary<string, List<Point>>? Paths { get; set; }

    public List<Dictionary<string, int>> Series { get; set; } = [];

    /// <summary>Written, but there is nothing to read it into.</summary>
    public int Total => Id * 3;
}
