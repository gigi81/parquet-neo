using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// The nested sample dataset: the schema and rows that tests/fixtures/nested_dataset.py describes, so that files written
/// by pyarrow can be compared with them. Change both together.
/// </summary>
internal static class NestedDataset
{
    public const int RowCount = 200;

    public static readonly string[] ColumnNames =
    [
        "id", "scores.list.element", "matrix.list.element.list.element", "person.name", "person.address.city", "person.address.zip",
        "events.list.element.kind", "events.list.element.count", "attributes.key_value.key", "attributes.key_value.value", "labels.list.element",
    ];

    /// <summary>A new schema instance (nodes belong to exactly one schema).</summary>
    public static ParquetSchema Schema() => new(
    [
        new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
        ListOf("scores", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional)),
        ListOf("matrix", FieldRepetitionType.Optional,
            ListOf("element", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional))),
        new GroupNode("person", FieldRepetitionType.Optional,
        [
            new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
            new GroupNode("address", FieldRepetitionType.Optional,
            [
                new PrimitiveNode("city", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
                new PrimitiveNode("zip", PhysicalType.Int32, FieldRepetitionType.Optional),
            ]),
        ]),
        ListOf("events", FieldRepetitionType.Optional,
            new GroupNode("element", FieldRepetitionType.Optional,
            [
                new PrimitiveNode("kind", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
                new PrimitiveNode("count", PhysicalType.Int32, FieldRepetitionType.Optional),
            ])),
        new GroupNode("attributes", FieldRepetitionType.Optional,
            [
                new GroupNode("key_value", FieldRepetitionType.Repeated,
                [
                    new PrimitiveNode("key", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
                    new PrimitiveNode("value", PhysicalType.Int32, FieldRepetitionType.Optional),
                ]),
            ],
            LogicalType.Map),
        ListOf("labels", FieldRepetitionType.Required, new PrimitiveNode("element", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)),
    ]);

    /// <summary>A LIST: a group holding one repeated group <c>list</c> that holds the <paramref name="element"/>.</summary>
    public static GroupNode ListOf(string name, FieldRepetitionType repetition, SchemaNode element) =>
        new(name, repetition, [new GroupNode("list", FieldRepetitionType.Repeated, [element])], LogicalType.List);

    public static IEnumerable<StructValue> Rows() => Enumerable.Range(0, RowCount).Select(Row);

    public static StructValue Row(int i) => new()
    {
        ["id"] = (long)i,
        ["scores"] = Scores(i),
        ["matrix"] = Matrix(i),
        ["person"] = Person(i),
        ["events"] = Events(i),
        ["attributes"] = Attributes(i),
        ["labels"] = Labels(i),
    };

    /// <summary>The value of a LIST group holding <paramref name="elements"/>.</summary>
    public static StructValue List(params object?[] elements) =>
        new() { ["list"] = elements.Select(element => new StructValue { ["element"] = element }).Cast<object?>().ToArray() };

    /// <summary>The value of a MAP group holding <paramref name="entries"/>.</summary>
    public static StructValue Map(params (string Key, object? Value)[] entries) =>
        new() { ["key_value"] = entries.Select(entry => new StructValue { ["key"] = entry.Key, ["value"] = entry.Value }).Cast<object?>().ToArray() };

    private static object? Scores(int i) => (i % 6) switch
    {
        0 => null,
        1 => List(),
        2 => List(i),
        3 => List(i, null, i + 1),
        4 => List((object?)null),
        _ => List(Enumerable.Range(0, i % 5 + 3).Cast<object?>().ToArray()),
    };

    private static object? Matrix(int i) => (i % 5) switch
    {
        0 => null,
        1 => List(),
        2 => List(List(1, 2), List(3)),
        3 => List(null, List(i)),
        _ => List(List(i), List(), List(i + 1, i + 2, i + 3)),
    };

    private static object? Person(int i) => (i % 4) switch
    {
        0 => null,
        1 => new StructValue { ["name"] = $"n{i}", ["address"] = new StructValue { ["city"] = $"c{i % 3}", ["zip"] = i } },
        2 => new StructValue { ["name"] = null, ["address"] = null },
        _ => new StructValue { ["name"] = $"n{i}", ["address"] = new StructValue { ["city"] = null, ["zip"] = null } },
    };

    private static StructValue Event(string? kind, int? count) => new() { ["kind"] = kind, ["count"] = count };

    private static object? Events(int i) => (i % 5) switch
    {
        0 => null,
        1 => List(),
        2 => List(Event($"k{i % 3}", i)),
        3 => List(Event(null, i), Event("x", null), null),
        _ => List(Enumerable.Range(0, i % 3 + 1).Select(n => (object?)Event($"k{i % 2}", i + n)).ToArray()),
    };

    private static object? Attributes(int i) => (i % 4) switch
    {
        0 => null,
        1 => Map(),
        2 => Map(("a", i), ("b", null)),
        _ => Map(($"k{i % 5}", i), ("z", 1)),
    };

    private static object? Labels(int i) => (i % 3) switch
    {
        0 => List(),
        1 => List($"l{i}"),
        _ => List("a", "b", "c"),
    };
}
