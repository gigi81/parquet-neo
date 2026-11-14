using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// Lists in the layouts older writers used (the backward-compatibility rules of the LIST type). The same rows are described
/// in tests/fixtures/verify_legacy_lists.py, which checks with pyarrow how the file reads back.
/// </summary>
internal static class LegacyListFile
{
    public const int RowCount = 24;

    public static readonly string[] ColumnNames =
    [
        "two_level.element", "struct_element.element.str", "struct_element.element.num", "tuple.tuple_tuple.str", "array.array.str", "implicit",
    ];

    public static ParquetSchema Schema() => new(
    [
        // optional group two_level (LIST) { repeated int32 element; }
        new GroupNode("two_level", FieldRepetitionType.Optional, [new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Repeated)], LogicalType.List),

        // optional group struct_element (LIST) { repeated group element { required binary str (STRING); required int32 num; } }
        new GroupNode("struct_element", FieldRepetitionType.Optional,
            [
                new GroupNode("element", FieldRepetitionType.Repeated,
                [
                    new PrimitiveNode("str", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
                    new PrimitiveNode("num", PhysicalType.Int32, FieldRepetitionType.Required),
                ]),
            ],
            LogicalType.List),

        // optional group tuple (LIST) { repeated group tuple_tuple { required binary str (STRING); } }
        new GroupNode("tuple", FieldRepetitionType.Optional,
            [new GroupNode("tuple_tuple", FieldRepetitionType.Repeated, [new PrimitiveNode("str", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)])],
            LogicalType.List),

        // optional group array (LIST) { repeated group array { required binary str (STRING); } }
        new GroupNode("array", FieldRepetitionType.Optional,
            [new GroupNode("array", FieldRepetitionType.Repeated, [new PrimitiveNode("str", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)])],
            LogicalType.List),

        // repeated int32 implicit; (not in any LIST group)
        new PrimitiveNode("implicit", PhysicalType.Int32, FieldRepetitionType.Repeated),
    ]);

    public static StructValue[] Rows() => Enumerable.Range(0, RowCount).Select(Row).ToArray();

    private static StructValue Str(string value) => new() { ["str"] = value };

    private static StructValue Row(int i) => new()
    {
        ["two_level"] = (i % 3) switch
        {
            0 => null,
            1 => new StructValue { ["element"] = new object?[] { } },
            _ => new StructValue { ["element"] = new object?[] { i, i + 1 } },
        },
        ["struct_element"] = (i % 3) switch
        {
            0 => null,
            1 => new StructValue { ["element"] = new object?[] { } },
            _ => new StructValue { ["element"] = new object?[] { new StructValue { ["str"] = $"s{i}", ["num"] = i }, new StructValue { ["str"] = "t", ["num"] = -i } } },
        },
        ["tuple"] = i % 2 == 0 ? null : new StructValue { ["tuple_tuple"] = new object?[] { Str($"a{i}"), Str("b") } },
        ["array"] = i % 2 == 0 ? new StructValue { ["array"] = new object?[] { } } : new StructValue { ["array"] = new object?[] { Str($"x{i}") } },
        ["implicit"] = Enumerable.Range(0, i % 3).Select(n => (object?)(i * 10 + n)).ToArray(),
    };
}
