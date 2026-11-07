using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Schema;

public class ParquetSchemaTests
{
    private const FieldRepetitionType Required = FieldRepetitionType.Required;
    private const FieldRepetitionType Optional = FieldRepetitionType.Optional;
    private const FieldRepetitionType Repeated = FieldRepetitionType.Repeated;

    /// <summary>Flat columns plus a list, a struct, a map and a repeated primitive.</summary>
    private static ParquetSchema NestedSchema() => new(
    [
        new PrimitiveNode("id", PhysicalType.Int64, Required),
        new PrimitiveNode("name", PhysicalType.ByteArray, Optional, LogicalType.String),
        new GroupNode("tags", Optional,
            [new GroupNode("list", Repeated, [new PrimitiveNode("element", PhysicalType.ByteArray, Optional, LogicalType.String)])],
            LogicalType.List),
        new GroupNode("point", Optional,
        [
            new PrimitiveNode("x", PhysicalType.Int32, Optional),
            new PrimitiveNode("y", PhysicalType.Int32, Required),
        ]),
        new GroupNode("attrs", Optional,
            [new GroupNode("key_value", Repeated,
            [
                new PrimitiveNode("key", PhysicalType.ByteArray, Required, LogicalType.String),
                new PrimitiveNode("value", PhysicalType.Int32, Optional),
            ])],
            LogicalType.Map),
        new PrimitiveNode("events", PhysicalType.Int64, Repeated),
    ]);

    [Test]
    public async Task Columns_AreListedInDepthFirstOrderWithPaths()
    {
        var schema = NestedSchema();

        var paths = schema.Columns.Select(c => string.Join('.', c.Path)).ToArray();

        await Assert.That(paths).IsEquivalentTo(new[]
        {
            "id", "name", "tags.list.element", "point.x", "point.y",
            "attrs.key_value.key", "attrs.key_value.value", "events",
        });
        await Assert.That(schema.Columns.Select(c => c.ColumnIndex)).IsEquivalentTo(Enumerable.Range(0, 8));
    }

    [Test]
    public async Task Levels_CountOptionalAndRepeatedAncestors()
    {
        var schema = NestedSchema();

        (string Path, int Definition, int Repetition)[] expected =
        [
            ("id", 0, 0),
            ("name", 1, 0),
            ("tags.list.element", 3, 1), // tags optional, list repeated, element optional
            ("point.x", 2, 0),
            ("point.y", 1, 0), // point optional, y required
            ("attrs.key_value.key", 2, 1),
            ("attrs.key_value.value", 3, 1),
            ("events", 1, 1),
        ];

        foreach (var (path, definition, repetition) in expected)
        {
            var column = schema.FindColumn(path.Split('.'))!;
            await Assert.That(column.MaxDefinitionLevel).IsEqualTo(definition);
            await Assert.That(column.MaxRepetitionLevel).IsEqualTo(repetition);
        }
    }

    [Test]
    public async Task GroupLevels_AreAvailableOnInnerNodes()
    {
        var schema = NestedSchema();
        var list = (GroupNode)((GroupNode)schema.Root.FindChild("tags")!).FindChild("list")!;

        await Assert.That(schema.Root.MaxDefinitionLevel).IsEqualTo(0);
        await Assert.That(list.MaxDefinitionLevel).IsEqualTo(2);
        await Assert.That(list.MaxRepetitionLevel).IsEqualTo(1);
        await Assert.That(list.Parent!.Name).IsEqualTo("tags");
        await Assert.That(schema.Root.Parent).IsNull();
        await Assert.That(schema.Root.Path.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FindColumn_ReturnsNullForUnknownPathsAndGroups()
    {
        var schema = NestedSchema();

        await Assert.That(schema.FindColumn(["tags", "list", "element"])).IsNotNull();
        await Assert.That(schema.FindColumn(["nope"])).IsNull();
        await Assert.That(schema.FindColumn(["tags"])).IsNull(); // a group, not a column
        await Assert.That(schema.FindColumn(["point", "x"])!.ColumnIndex).IsEqualTo(3);
    }

    [Test]
    public async Task ToElements_FlattensDepthFirstWithChildCounts()
    {
        var elements = NestedSchema().ToElements();

        await Assert.That(elements.Select(e => e.Name)).IsEquivalentTo(new[]
        {
            "schema", "id", "name", "tags", "list", "element", "point", "x", "y",
            "attrs", "key_value", "key", "value", "events",
        });
        await Assert.That(elements[0].NumChildren).IsEqualTo(6);
        await Assert.That(elements[0].RepetitionType).IsNull(); // the root has no repetition
        await Assert.That(elements[0].Type).IsNull();
        await Assert.That(elements[3].NumChildren).IsEqualTo(1); // tags
        await Assert.That(elements[1].Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(elements[1].NumChildren).IsNull();
        await Assert.That(elements[3].LogicalType).IsEqualTo(LogicalType.List);
        await Assert.That(elements[3].ConvertedType).IsEqualTo(ConvertedType.List);
        await Assert.That(elements[4].RepetitionType).IsEqualTo(Repeated);
    }

    [Test]
    public async Task ToElements_WritesBothLogicalAndLegacyAnnotations()
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("amount", PhysicalType.FixedLenByteArray, Optional, LogicalType.Decimal(2, 10), typeLength: 5),
            new PrimitiveNode("local", PhysicalType.Int64, Required, LogicalType.Timestamp(isAdjustedToUtc: false, TimeUnit.Micros)),
            new PrimitiveNode("nanos", PhysicalType.Int64, Required, LogicalType.Timestamp(true, TimeUnit.Nanos)),
            new PrimitiveNode("small", PhysicalType.Int32, Required, LogicalType.Integer(8, isSigned: false), fieldId: 7),
        ]);

        var elements = schema.ToElements();

        var amount = elements[1];
        await Assert.That(amount.Type).IsEqualTo(PhysicalType.FixedLenByteArray);
        await Assert.That(amount.TypeLength).IsEqualTo(5);
        await Assert.That(amount.ConvertedType).IsEqualTo(ConvertedType.Decimal);
        await Assert.That(amount.Scale).IsEqualTo(2);
        await Assert.That(amount.Precision).IsEqualTo(10);
        await Assert.That(amount.LogicalType).IsEqualTo(LogicalType.Decimal(2, 10));
        await Assert.That(elements[2].ConvertedType).IsEqualTo(ConvertedType.TimestampMicros);
        await Assert.That(elements[3].ConvertedType).IsNull();
        await Assert.That(elements[3].LogicalType!.Kind).IsEqualTo(LogicalTypeKind.Timestamp);
        await Assert.That(elements[4].ConvertedType).IsEqualTo(ConvertedType.UInt8);
        await Assert.That(elements[4].FieldId).IsEqualTo(7);
    }

    [Test]
    public async Task SchemaSurvivesTheElementRoundTrip()
    {
        var original = NestedSchema().ToElements();

        var rebuilt = ParquetSchema.FromElements(original);

        await Assert.That(rebuilt.ToElements().Select(TestBytes.ToHex))
            .IsEquivalentTo(original.Select(TestBytes.ToHex));
        await Assert.That(rebuilt.Columns.Count).IsEqualTo(8);
        await Assert.That(rebuilt.FindColumn(["tags", "list", "element"])!.MaxDefinitionLevel).IsEqualTo(3);
    }

    [Test]
    public async Task FromElements_AcceptsAnEmptySchema()
    {
        var schema = ParquetSchema.FromElements([new SchemaElement { Name = "schema", NumChildren = 0 }]);

        await Assert.That(schema.Columns.Count).IsEqualTo(0);
        await Assert.That(schema.Root.Name).IsEqualTo("schema");
    }

    [Test]
    public async Task FromElements_ResolvesLegacyConvertedTypes()
    {
        var schema = ParquetSchema.FromElements(
        [
            Root(3),
            Leaf("ts", PhysicalType.Int64, convertedType: ConvertedType.TimestampMicros),
            Leaf("price", PhysicalType.Int32, convertedType: ConvertedType.Decimal, scale: 2, precision: 9),
            Leaf("text", PhysicalType.ByteArray, convertedType: ConvertedType.Utf8),
        ]);

        await Assert.That(schema.Columns[0].LogicalType).IsEqualTo(LogicalType.Timestamp(true, TimeUnit.Micros));
        await Assert.That(schema.Columns[0].ConvertedType).IsEqualTo(ConvertedType.TimestampMicros);
        await Assert.That(schema.Columns[1].LogicalType).IsEqualTo(LogicalType.Decimal(2, 9));
        await Assert.That(schema.Columns[2].LogicalType).IsEqualTo(LogicalType.String);
    }

    [Test]
    public async Task FromElements_PrefersTheLogicalTypeOverTheLegacyOne()
    {
        var element = Leaf("t", PhysicalType.Int64, convertedType: ConvertedType.TimestampMicros);
        element.LogicalType = LogicalType.Timestamp(isAdjustedToUtc: false, TimeUnit.Micros);

        var schema = ParquetSchema.FromElements([Root(1), element]);

        await Assert.That(schema.Columns[0].LogicalType).IsEqualTo(LogicalType.Timestamp(false, TimeUnit.Micros));
    }

    [Test]
    public async Task FromElements_IgnoresAnnotationsThatDoNotSuitTheColumn()
    {
        var schema = ParquetSchema.FromElements(
        [
            Root(4),
            Leaf("a", PhysicalType.Int32, convertedType: ConvertedType.Utf8),
            Leaf("b", PhysicalType.Int32, convertedType: ConvertedType.Decimal, scale: 0, precision: 12),
            Leaf("c", PhysicalType.Int64, logicalType: LogicalType.Uuid),
            Leaf("d", PhysicalType.Int32, convertedType: ConvertedType.Decimal), // no precision
        ]);

        foreach (var column in schema.Columns)
        {
            await Assert.That(column.LogicalType).IsNull();
        }

        await Assert.That(schema.Columns[0].ConvertedType).IsEqualTo(ConvertedType.Utf8); // still reported as read
    }

    [Test]
    public async Task FromElements_KeepsLegacyOnlyAnnotationsOnGroupsAndColumns()
    {
        var group = new SchemaElement
        {
            Name = "kv",
            RepetitionType = Repeated,
            NumChildren = 1,
            ConvertedType = ConvertedType.MapKeyValue,
        };
        var interval = Leaf("span", PhysicalType.FixedLenByteArray, convertedType: ConvertedType.Interval, typeLength: 12);

        var schema = ParquetSchema.FromElements([Root(2), group, Leaf("v", PhysicalType.Int32), interval]);

        var kv = (GroupNode)schema.Root.Children[0];
        await Assert.That(kv.ConvertedType).IsEqualTo(ConvertedType.MapKeyValue);
        await Assert.That(kv.LogicalType).IsNull();
        await Assert.That(schema.Columns[1].ConvertedType).IsEqualTo(ConvertedType.Interval);
        await Assert.That(schema.Columns[1].TypeLength).IsEqualTo(12);
    }

    [Test]
    public async Task FromElements_ToleratesATypeLengthOnNonFixedColumns()
    {
        var schema = ParquetSchema.FromElements([Root(1), Leaf("n", PhysicalType.Int32, typeLength: 4)]);

        await Assert.That(schema.Columns[0].TypeLength).IsNull();
    }

    [Test]
    public async Task FromElements_RejectsMalformedTrees()
    {
        (string Reason, SchemaElement[] Elements)[] cases =
        [
            ("no elements", []),
            ("root with a physical type", [Leaf("root", PhysicalType.Int32)]),
            ("missing repetition", [Root(1), new SchemaElement { Name = "c", Type = PhysicalType.Int32 }]),
            ("unknown repetition", [Root(1), Leaf("c", PhysicalType.Int32, repetition: (FieldRepetitionType)9)]),
            ("unknown physical type", [Root(1), Leaf("c", (PhysicalType)99)]),
            ("fixed length without length", [Root(1), Leaf("c", PhysicalType.FixedLenByteArray)]),
            ("fixed length with zero length", [Root(1), Leaf("c", PhysicalType.FixedLenByteArray, typeLength: 0)]),
            ("column with children", [Root(1), new SchemaElement
                { Name = "c", Type = PhysicalType.Int32, RepetitionType = Required, NumChildren = 2 }]),
            ("fewer elements than declared", [Root(2), Leaf("a", PhysicalType.Int32)]),
            ("child count beyond the end", [Root(1), Group("g", 3), Leaf("a", PhysicalType.Int32)]),
            ("elements after the tree", [Root(1), Leaf("a", PhysicalType.Int32), Leaf("b", PhysicalType.Int32)]),
            ("negative child count", [new SchemaElement { Name = "schema", NumChildren = -1 }]),
            ("group without repetition", [Root(1), new SchemaElement { Name = "g", NumChildren = 0 }]),
        ];

        foreach (var (reason, elements) in cases)
        {
            await Assert.That(() => ParquetSchema.FromElements(elements))
                .Throws<InvalidSchemaException>()
                .Because(reason);
        }
    }

    [Test]
    public async Task FromElements_HandlesVeryDeepSchemasWithoutRecursing()
    {
        const int depth = 100_000;
        var elements = new List<SchemaElement>(depth + 2) { Root(1) };
        for (int i = 0; i < depth; i++)
        {
            elements.Add(Group("g", 1));
        }

        elements.Add(Leaf("leaf", PhysicalType.Int32));

        var schema = ParquetSchema.FromElements(elements);
        var leaf = schema.Columns.Single();

        await Assert.That(leaf.MaxDefinitionLevel).IsEqualTo(depth + 1); // every group and the leaf are optional
        await Assert.That(leaf.Path.Length).IsEqualTo(depth + 1);
        await Assert.That(schema.ToElements().Count).IsEqualTo(depth + 2);
    }

    [Test]
    public async Task Constructors_RejectInvalidNodes()
    {
        (string Reason, Action Build)[] cases =
        [
            ("fixed length without length", () => new PrimitiveNode("c", PhysicalType.FixedLenByteArray, Required)),
            ("string on int32", () => new PrimitiveNode("c", PhysicalType.Int32, Required, LogicalType.String)),
            ("decimal precision too large", () =>
                new PrimitiveNode("c", PhysicalType.Int32, Required, LogicalType.Decimal(0, 10))),
            ("uuid of the wrong length", () =>
                new PrimitiveNode("c", PhysicalType.FixedLenByteArray, Required, LogicalType.Uuid, typeLength: 8)),
            ("list on a column", () => new PrimitiveNode("c", PhysicalType.Int32, Required, LogicalType.List)),
            ("unrecognized logical type", () =>
                new PrimitiveNode("c", PhysicalType.Int32, Required, LogicalType.Unrecognized)),
            ("string on a group", () => new GroupNode("g", Required, [], LogicalType.String)),
            ("unknown repetition", () => new PrimitiveNode("c", PhysicalType.Int32, (FieldRepetitionType)7)),
            ("null name", () => new PrimitiveNode(null!, PhysicalType.Int32, Required)),
            ("null child", () => new GroupNode("g", Required, [null!])),
        ];

        foreach (var (reason, build) in cases)
        {
            await Assert.That(() => build()).Throws<Exception>().Because(reason);
        }
    }

    [Test]
    public async Task Constructor_DerivesTheLegacyAnnotation()
    {
        var text = new PrimitiveNode("s", PhysicalType.ByteArray, Optional, LogicalType.String);
        var plain = new PrimitiveNode("i", PhysicalType.Int32, Required);

        await Assert.That(text.ConvertedType).IsEqualTo(ConvertedType.Utf8);
        await Assert.That(plain.LogicalType).IsNull();
        await Assert.That(plain.ConvertedType).IsNull();
    }

    [Test]
    public async Task ANodeCanOnlyBelongToOneSchema()
    {
        var shared = new PrimitiveNode("id", PhysicalType.Int64, Required);
        _ = new ParquetSchema([shared]);

        await Assert.That(() => new ParquetSchema([shared])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ADuplicatedChildInOneGroupIsRejected()
    {
        var node = new PrimitiveNode("id", PhysicalType.Int64, Required);

        await Assert.That(() => new ParquetSchema([node, node])).Throws<InvalidOperationException>();
    }

    private static SchemaElement Root(int children) => new() { Name = "schema", NumChildren = children };

    private static SchemaElement Group(string name, int children) =>
        new() { Name = name, RepetitionType = Optional, NumChildren = children };

    private static SchemaElement Leaf(
        string name,
        PhysicalType type,
        FieldRepetitionType repetition = Optional,
        ConvertedType? convertedType = null,
        LogicalType? logicalType = null,
        int? scale = null,
        int? precision = null,
        int? typeLength = null) =>
        new()
        {
            Name = name,
            Type = type,
            RepetitionType = repetition,
            ConvertedType = convertedType,
            LogicalType = logicalType,
            Scale = scale,
            Precision = precision,
            TypeLength = typeLength,
        };
}
