using ParquetNeo.DataModel;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests;
using static ParquetNeo.Schema.SchemaField;

namespace ParquetNeo.Serialization.Tests;

/// <summary>The schema a <see cref="ParquetSchemaBuilder"/> describes is the schema the serializer writes for the same .NET types.</summary>
public class SchemaBuilderTests
{
    private enum Level
    {
        Low,
        High,
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";

        public int? Zip { get; set; }
    }

    private sealed class Row
    {
        public bool Flag { get; set; }

        public sbyte I8 { get; set; }

        public short I16 { get; set; }

        public int I32 { get; set; }

        public long I64 { get; set; }

        public byte U8 { get; set; }

        public ushort U16 { get; set; }

        public uint U32 { get; set; }

        public ulong U64 { get; set; }

        public float F32 { get; set; }

        public double F64 { get; set; }

        public Half Half { get; set; }

        public string Text { get; set; } = "";

        public byte[] Bytes { get; set; } = [];

        public Guid Guid { get; set; }

        public DateOnly Date { get; set; }

        public TimeOnly Time { get; set; }

        [ParquetTime(TimeUnit.Millis)]
        public TimeOnly TimeMillis { get; set; }

        public DateTime Moment { get; set; }

        [ParquetTimestamp(TimeUnit.Nanos, IsAdjustedToUtc = false)]
        public DateTime LocalNanos { get; set; }

        public decimal Money { get; set; }

        [ParquetDecimal(9, 2)]
        public decimal Small { get; set; }

        [ParquetDecimal(18, 4)]
        public decimal? Medium { get; set; }

        public Level Level { get; set; }

        public int? MaybeInt { get; set; }

        public string? MaybeText { get; set; }

        public Address Home { get; set; } = new();

        public Address? Work { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<int?>? Numbers { get; set; }

        public Dictionary<string, int?> Scores { get; set; } = [];

        public Dictionary<string, List<Address?>>? Places { get; set; }
    }

    [Test]
    public async Task TheBuilder_DescribesTheSchemaOfTheSerializer()
    {
        var built = new ParquetSchemaBuilder()
            .Boolean("Flag")
            .Int8("I8")
            .Int16("I16")
            .Int32("I32")
            .Int64("I64")
            .UInt8("U8")
            .UInt16("U16")
            .UInt32("U32")
            .UInt64("U64")
            .Float("F32")
            .Double("F64")
            .Half("Half")
            .String("Text")
            .Binary("Bytes")
            .Guid("Guid")
            .Date("Date")
            .Time("Time")
            .Time("TimeMillis", TimeUnit.Millis)
            .Timestamp("Moment")
            .Timestamp("LocalNanos", TimeUnit.Nanos, utc: false)
            .Decimal("Money", 38, 18)
            .Decimal("Small", 9, 2)
            .Decimal("Medium", 18, 4, optional: true)
            .Enum("Level")
            .Int32("MaybeInt", optional: true)
            .String("MaybeText", optional: true)
            .Struct("Home", home => home.String("Street").Int32("Zip", optional: true))
            .Struct("Work", optional: true, work => work.String("Street").Int32("Zip", optional: true))
            .List("Tags", String())
            .List("Numbers", Int32(optional: true), optional: true)
            .Map("Scores", String(), Int32(optional: true))
            .Map("Places", String(), List(Struct(a => a.String("Street").Int32("Zip", optional: true), optional: true)), optional: true)
            .Build();

        await Assert.That(SchemaDescription.Of(built)).IsEqualTo(SchemaDescription.Of(ParquetSerializer.GetSchema<Row>()));
    }

    [Test]
    public async Task ASchemaOfTheBuilder_WritesRowsTheSerializerReads()
    {
        var schema = new ParquetSchemaBuilder()
            .Int32("I32")
            .String("Text")
            .Decimal("Small", 9, 2)
            .Build();
        var stream = new MemoryStream();

        using (var writer = ParquetFileWriter.Create(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(new[] { 1, 2 });
            group.NextColumn().WriteStrings(["a", "b"]);
            group.NextColumn().WriteBatch(new[] { 150, -275 }); // the unscaled values of 1.50 and -2.75
        }

        var rows = ParquetSerializer.Deserialize<Mixed>(new MemoryStream(stream.ToArray()));

        await Assert.That(rows.Select(r => r.I32).SequenceEqual([1, 2])).IsTrue();
        await Assert.That(rows.Select(r => r.Text).SequenceEqual(["a", "b"])).IsTrue();
        await Assert.That(rows.Select(r => r.Small).SequenceEqual([1.50m, -2.75m])).IsTrue();
    }

    private sealed class Mixed
    {
        public int I32 { get; set; }

        public string Text { get; set; } = "";

        [ParquetDecimal(9, 2)]
        public decimal Small { get; set; }
    }
}
