using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// The Document example from the Dremel paper (Figures 2 and 3): the one case whose repetition and definition levels are
/// published, which anchors the reference shredder to something other than itself.
/// </summary>
internal static class DocumentExample
{
    public static ParquetSchema Schema() => new(
    [
        new PrimitiveNode("DocId", PhysicalType.Int64, FieldRepetitionType.Required),
        new GroupNode("Links", FieldRepetitionType.Optional,
        [
            new PrimitiveNode("Backward", PhysicalType.Int64, FieldRepetitionType.Repeated),
            new PrimitiveNode("Forward", PhysicalType.Int64, FieldRepetitionType.Repeated),
        ]),
        new GroupNode("Name", FieldRepetitionType.Repeated,
        [
            new GroupNode("Language", FieldRepetitionType.Repeated,
            [
                new PrimitiveNode("Code", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
                new PrimitiveNode("Country", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
            ]),
            new PrimitiveNode("Url", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        ]),
    ]);

    private static StructValue Language(string code, string? country) => new() { ["Code"] = code, ["Country"] = country };

    public static StructValue[] Rows() =>
    [
        new()
        {
            ["DocId"] = 10L,
            ["Links"] = new StructValue { ["Forward"] = new object?[] { 20L, 40L, 60L } },
            ["Name"] = new object?[]
            {
                new StructValue { ["Language"] = new object?[] { Language("en-us", "us"), Language("en", null) }, ["Url"] = "http://A" },
                new StructValue { ["Url"] = "http://B" },
                new StructValue { ["Language"] = new object?[] { Language("en-gb", "gb") } },
            },
        },
        new()
        {
            ["DocId"] = 20L,
            ["Links"] = new StructValue { ["Backward"] = new object?[] { 10L, 30L }, ["Forward"] = new object?[] { 80L } },
            ["Name"] = new object?[] { new StructValue { ["Url"] = "http://C" } },
        },
    ];

    /// <summary>The columns of Figure 3: (definition level, repetition level, value or null) per entry, in schema order.</summary>
    public static (string Path, (int Definition, int Repetition, object? Value)[] Entries)[] Expected() =>
    [
        ("DocId", [(0, 0, 10L), (0, 0, 20L)]),
        ("Links.Backward", [(1, 0, null), (2, 0, 10L), (2, 1, 30L)]),
        ("Links.Forward", [(2, 0, 20L), (2, 1, 40L), (2, 1, 60L), (2, 0, 80L)]),
        ("Name.Language.Code", [(2, 0, "en-us"), (2, 2, "en"), (1, 1, null), (2, 1, "en-gb"), (1, 0, null)]),
        ("Name.Language.Country", [(3, 0, "us"), (2, 2, null), (1, 1, null), (3, 1, "gb"), (1, 0, null)]),
        ("Name.Url", [(2, 0, "http://A"), (2, 1, "http://B"), (1, 1, null), (2, 0, "http://C")]),
    ];
}
