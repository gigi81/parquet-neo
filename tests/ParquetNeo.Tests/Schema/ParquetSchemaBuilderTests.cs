using ParquetNeo.Metadata;
using ParquetNeo.Nested;
using ParquetNeo.Schema;
using static ParquetNeo.Schema.SchemaField;

namespace ParquetNeo.Tests.Schema;

public class ParquetSchemaBuilderTests
{
    [Test]
    public async Task AFlatSchema_IsTheSameAsTheOneMadeByHand()
    {
        var byHand = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("Name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
            new PrimitiveNode("Score", PhysicalType.Double, FieldRepetitionType.Optional),
            new PrimitiveNode("Born", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Date),
        ]);

        var built = new ParquetSchemaBuilder()
            .Int64("Id")
            .String("Name", optional: true)
            .Double("Score", optional: true)
            .Date("Born")
            .Build();

        await Assert.That(SchemaDescription.Of(built)).IsEqualTo(SchemaDescription.Of(byHand));
        await Assert.That(built.Columns.Select(c => c.Name).SequenceEqual(["Id", "Name", "Score", "Born"])).IsTrue();
    }

    [Test]
    public async Task EveryFieldIsRequired_UnlessItIsOptional()
    {
        var schema = new ParquetSchemaBuilder().Int32("A").Int32("B", optional: true).Build();

        await Assert.That(schema.Columns[0].Repetition).IsEqualTo(FieldRepetitionType.Required);
        await Assert.That(schema.Columns[1].Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(schema.Columns[0].MaxDefinitionLevel).IsEqualTo(0);
        await Assert.That(schema.Columns[1].MaxDefinitionLevel).IsEqualTo(1);
    }

    public static IEnumerable<(string Name, Func<ParquetSchemaBuilder, ParquetSchemaBuilder> Add, PhysicalType Type, LogicalType? Logical, int? Length)> Types() =>
    [
        ("Boolean", b => b.Boolean("c"), PhysicalType.Boolean, null, null),
        ("Int8", b => b.Int8("c"), PhysicalType.Int32, LogicalType.Integer(8, true), null),
        ("Int16", b => b.Int16("c"), PhysicalType.Int32, LogicalType.Integer(16, true), null),
        ("Int32", b => b.Int32("c"), PhysicalType.Int32, null, null),
        ("Int64", b => b.Int64("c"), PhysicalType.Int64, null, null),
        ("UInt8", b => b.UInt8("c"), PhysicalType.Int32, LogicalType.Integer(8, false), null),
        ("UInt16", b => b.UInt16("c"), PhysicalType.Int32, LogicalType.Integer(16, false), null),
        ("UInt32", b => b.UInt32("c"), PhysicalType.Int32, LogicalType.Integer(32, false), null),
        ("UInt64", b => b.UInt64("c"), PhysicalType.Int64, LogicalType.Integer(64, false), null),
        ("Float", b => b.Float("c"), PhysicalType.Float, null, null),
        ("Double", b => b.Double("c"), PhysicalType.Double, null, null),
        ("Half", b => b.Half("c"), PhysicalType.FixedLenByteArray, LogicalType.Float16, 2),
        ("String", b => b.String("c"), PhysicalType.ByteArray, LogicalType.String, null),
        ("Enum", b => b.Enum("c"), PhysicalType.ByteArray, LogicalType.Enum, null),
        ("Json", b => b.Json("c"), PhysicalType.ByteArray, LogicalType.Json, null),
        ("Binary", b => b.Binary("c"), PhysicalType.ByteArray, null, null),
        ("FixedBinary", b => b.FixedBinary("c", 12), PhysicalType.FixedLenByteArray, null, 12),
        ("Guid", b => b.Guid("c"), PhysicalType.FixedLenByteArray, LogicalType.Uuid, 16),
        ("Date", b => b.Date("c"), PhysicalType.Int32, LogicalType.Date, null),
        ("Time", b => b.Time("c"), PhysicalType.Int64, LogicalType.Time(false, TimeUnit.Micros), null),
        ("TimeMillis", b => b.Time("c", TimeUnit.Millis, utc: true), PhysicalType.Int32, LogicalType.Time(true, TimeUnit.Millis), null),
        ("TimeNanos", b => b.Time("c", TimeUnit.Nanos), PhysicalType.Int64, LogicalType.Time(false, TimeUnit.Nanos), null),
        ("Timestamp", b => b.Timestamp("c"), PhysicalType.Int64, LogicalType.Timestamp(true, TimeUnit.Micros), null),
        ("TimestampMillisLocal", b => b.Timestamp("c", TimeUnit.Millis, utc: false), PhysicalType.Int64, LogicalType.Timestamp(false, TimeUnit.Millis), null),
        ("Decimal", b => b.Decimal("c", 9, 2), PhysicalType.Int32, LogicalType.Decimal(2, 9), null),
        ("Column", b => b.Column("c", PhysicalType.ByteArray, LogicalType.Bson), PhysicalType.ByteArray, LogicalType.Bson, null),
    ];

    [Test]
    [MethodDataSource(nameof(Types))]
    public async Task EachType_IsTheColumnOfItsPhysicalAndLogicalType(string name, Func<ParquetSchemaBuilder, ParquetSchemaBuilder> add, PhysicalType type, LogicalType? logical, int? length)
    {
        var column = add(new ParquetSchemaBuilder()).Build().Columns.Single();

        await Assert.That(column.Name).IsEqualTo("c").Because(name);
        await Assert.That(column.Type).IsEqualTo(type).Because(name);
        await Assert.That(column.LogicalType).IsEqualTo(logical).Because(name);
        await Assert.That(column.TypeLength).IsEqualTo(length).Because(name);
        await Assert.That(column.ConvertedType).IsEqualTo(ExpectedConverted(logical)).Because(name);
    }

    private static ConvertedType? ExpectedConverted(LogicalType? logical) => logical?.Kind switch
    {
        LogicalTypeKind.String => ConvertedType.Utf8,
        LogicalTypeKind.Enum => ConvertedType.Enum,
        LogicalTypeKind.Json => ConvertedType.Json,
        LogicalTypeKind.Bson => ConvertedType.Bson,
        LogicalTypeKind.Date => ConvertedType.Date,
        LogicalTypeKind.Decimal => ConvertedType.Decimal,
        LogicalTypeKind.Time => logical.Unit switch { TimeUnit.Millis => ConvertedType.TimeMillis, TimeUnit.Micros => ConvertedType.TimeMicros, _ => null },
        LogicalTypeKind.Timestamp => logical.Unit switch { TimeUnit.Millis => ConvertedType.TimestampMillis, TimeUnit.Micros => ConvertedType.TimestampMicros, _ => null },
        LogicalTypeKind.Integer => (logical.BitWidth, logical.IsSigned) switch
        {
            (8, true) => ConvertedType.Int8,
            (16, true) => ConvertedType.Int16,
            (8, false) => ConvertedType.UInt8,
            (16, false) => ConvertedType.UInt16,
            (32, false) => ConvertedType.UInt32,
            (64, false) => ConvertedType.UInt64,
            _ => null,
        },
        _ => null,
    };

    [Test]
    [Arguments(1, PhysicalType.Int32, null)]
    [Arguments(9, PhysicalType.Int32, null)]
    [Arguments(10, PhysicalType.Int64, null)]
    [Arguments(18, PhysicalType.Int64, null)]
    [Arguments(19, PhysicalType.FixedLenByteArray, 9)]
    [Arguments(28, PhysicalType.FixedLenByteArray, 12)]
    [Arguments(38, PhysicalType.FixedLenByteArray, 16)]
    public async Task ADecimal_IsStoredInTheSmallestPhysicalType(int precision, PhysicalType type, int? length)
    {
        var column = new ParquetSchemaBuilder().Decimal("d", precision, scale: Math.Min(2, precision)).Build().Columns.Single();

        await Assert.That(column.Type).IsEqualTo(type);
        await Assert.That(column.TypeLength).IsEqualTo(length);
        await Assert.That(column.LogicalType!.Precision).IsEqualTo(precision);
    }

    [Test]
    public async Task ADecimal_NeedsAPrecisionAndAScaleInRange()
    {
        await Assert.That(() => Decimal(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Decimal(39)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Decimal(10, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Decimal(10, 11)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(Decimal(10, 10)).IsNotNull();
        await Assert.That(Decimal(38, 38)).IsNotNull();
    }

    [Test]
    public async Task AStruct_HoldsItsFieldsOneLevelDown()
    {
        var schema = new ParquetSchemaBuilder()
            .Int32("Id")
            .Struct("Address", optional: true, address => address
                .String("City", optional: true)
                .Int32("Zip")
                .Struct("Geo", geo => geo.Double("Lat").Double("Lon")))
            .Build();

        await Assert.That(schema.Columns.Select(c => string.Join('.', c.Path)).SequenceEqual(["Id", "Address.City", "Address.Zip", "Address.Geo.Lat", "Address.Geo.Lon"])).IsTrue();
        await Assert.That(schema.FindColumn(["Address", "City"])!.MaxDefinitionLevel).IsEqualTo(2);
        await Assert.That(schema.FindColumn(["Address", "Zip"])!.MaxDefinitionLevel).IsEqualTo(1);
        await Assert.That(schema.FindColumn(["Address", "Geo", "Lat"])!.MaxDefinitionLevel).IsEqualTo(1);
        await Assert.That(schema.Root.FindChild("Address")!.Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(((GroupNode)schema.Root.FindChild("Address")!).FindChild("Geo")!.Repetition).IsEqualTo(FieldRepetitionType.Required);
    }

    [Test]
    public async Task AList_IsInTheStandardLayout_AndTheSameAsTheOneOfNestedSchema()
    {
        var built = new ParquetSchemaBuilder()
            .List("Tags", String(optional: true), optional: true)
            .List("Numbers", Int32())
            .Build();
        var byHand = new ParquetSchema(
        [
            NestedSchema.List("Tags", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String)),
            NestedSchema.List("Numbers", FieldRepetitionType.Required, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Required)),
        ]);

        await Assert.That(SchemaDescription.Of(built)).IsEqualTo(SchemaDescription.Of(byHand));
        var tags = built.Columns[0];
        await Assert.That(string.Join('.', tags.Path)).IsEqualTo("Tags.list.element");
        await Assert.That(tags.MaxDefinitionLevel).IsEqualTo(3);
        await Assert.That(tags.MaxRepetitionLevel).IsEqualTo(1);
        await Assert.That(built.Columns[1].MaxDefinitionLevel).IsEqualTo(1);
    }

    [Test]
    public async Task AMap_IsInTheStandardLayout_AndTheSameAsTheOneOfNestedSchema()
    {
        var built = new ParquetSchemaBuilder().Map("Scores", String(), Int32(optional: true), optional: true).Build();
        var byHand = new ParquetSchema(
        [
            NestedSchema.Map(
                "Scores",
                FieldRepetitionType.Optional,
                new PrimitiveNode("key", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
                new PrimitiveNode("value", PhysicalType.Int32, FieldRepetitionType.Optional)),
        ]);

        await Assert.That(SchemaDescription.Of(built)).IsEqualTo(SchemaDescription.Of(byHand));
        await Assert.That(built.Columns.Select(c => string.Join('.', c.Path)).SequenceEqual(["Scores.key_value.key", "Scores.key_value.value"])).IsTrue();
    }

    [Test]
    public async Task ListsAndMapsOfStructsAndOfEachOther_Nest()
    {
        var schema = new ParquetSchemaBuilder()
            .List("Items", Struct(item => item.String("Sku").Decimal("Price", 12, 2, optional: true), optional: true))
            .Map("Index", String(), List(Int64()))
            .List("Grid", List(Double()))
            .Build();

        await Assert.That(schema.Columns.Select(c => string.Join('.', c.Path)).SequenceEqual(
            ["Items.list.element.Sku", "Items.list.element.Price", "Index.key_value.key", "Index.key_value.value.list.element", "Grid.list.element.list.element"])).IsTrue();
        await Assert.That(schema.FindColumn(["Grid", "list", "element", "list", "element"])!.MaxRepetitionLevel).IsEqualTo(2);
        await Assert.That(schema.FindColumn(["Items", "list", "element", "Price"])!.MaxDefinitionLevel).IsEqualTo(3);
    }

    [Test]
    public async Task AFieldDefinition_CanBeUsedForSeveralNames()
    {
        var price = Decimal(18, 4, optional: true);

        var schema = new ParquetSchemaBuilder().Field("Bid", price).Field("Ask", price).Struct("Last", s => s.Field("Price", price)).Build();

        await Assert.That(schema.Columns.Count).IsEqualTo(3);
        await Assert.That(schema.Columns.All(c => c.LogicalType!.Kind == LogicalTypeKind.Decimal && c.Repetition == FieldRepetitionType.Optional)).IsTrue();
    }

    [Test]
    public async Task ABuilder_MakesSchemasThatDoNotShareNodes()
    {
        var builder = new ParquetSchemaBuilder().Int32("A").Struct("S", s => s.String("B"));

        var first = builder.Build("first");
        var second = builder.Build("second");

        await Assert.That(first.Root.Name).IsEqualTo("first");
        await Assert.That(second.Root.Name).IsEqualTo("second");
        await Assert.That(ReferenceEquals(first.Columns[0], second.Columns[0])).IsFalse();
        await Assert.That(SchemaDescription.Of(first).Replace("first", "x")).IsEqualTo(SchemaDescription.Of(second).Replace("second", "x"));
    }

    [Test]
    public async Task ANodeMadeByHand_CanBeAdded_ButBelongsToOneSchema()
    {
        var builder = new ParquetSchemaBuilder()
            .Int32("A")
            .Add(new PrimitiveNode("Legacy", PhysicalType.Int32, FieldRepetitionType.Repeated));

        var schema = builder.Build();

        await Assert.That(schema.FindColumn(["Legacy"])!.MaxRepetitionLevel).IsEqualTo(1);
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheSchema_IsWrittenInTheFooterAndReadBack()
    {
        var schema = new ParquetSchemaBuilder()
            .Int64("Id")
            .Decimal("Amount", 30, 6, optional: true)
            .Timestamp("At", TimeUnit.Millis)
            .Struct("S", optional: true, s => s.Guid("G").Half("H", optional: true))
            .List("L", String())
            .Map("M", Int32(), Json(optional: true))
            .Build("root");
        var stream = new MemoryStream();

        using (var writer = ParquetFileWriter.Create(stream, schema, leaveOpen: true))
        {
            // The writer needs the file to hold a row group of every column to be valid; the footer is what is read back.
            writer.Dispose();
        }

        using var reader = ParquetFileReader.Open(new MemoryStream(stream.ToArray()));

        await Assert.That(SchemaDescription.Of(reader.Schema)).IsEqualTo(SchemaDescription.Of(schema));
    }

    [Test]
    public async Task AFieldWithAProblem_IsRefused()
    {
        var builder = new ParquetSchemaBuilder().Int32("A");

        await Assert.That(() => builder.Int32("A")).Throws<ArgumentException>();
        await Assert.That(() => builder.Struct("A", s => s.Int32("B"))).Throws<ArgumentException>();
        await Assert.That(() => builder.Int32("")).Throws<ArgumentException>();
        await Assert.That(() => builder.Int32(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Field("X", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Add(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Add(new PrimitiveNode("A", PhysicalType.Int32, FieldRepetitionType.Required))).Throws<ArgumentException>();
        await Assert.That(() => builder.Struct("S", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.List("L", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Map("M", null!, Int32())).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Map("M", Int32(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.Map("M", Int32(optional: true), Int32())).Throws<ArgumentException>();
        await Assert.That(() => builder.FixedBinary("F", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => builder.Time("T", (TimeUnit)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => builder.Timestamp("T", (TimeUnit)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(builder.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnAnnotationThatDoesNotSuitItsType_IsRefusedWhenTheSchemaIsBuilt()
    {
        var builder = new ParquetSchemaBuilder().Column("A", PhysicalType.Int32, LogicalType.Json);

        await Assert.That(() => builder.Build()).Throws<ArgumentException>();
        await Assert.That(() => new ParquetSchemaBuilder().Column("A", PhysicalType.FixedLenByteArray).Build()).Throws<ArgumentException>();
    }

    [Test]
    public async Task ASchemaWithNoFieldsAndAStructWithNone_AreRefused()
    {
        await Assert.That(() => new ParquetSchemaBuilder().Build()).Throws<InvalidOperationException>();
        await Assert.That(() => new ParquetSchemaBuilder().Struct("S", _ => { }).Build()).Throws<InvalidOperationException>();
    }
}
