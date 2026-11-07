using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class SerializerModelTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    // ---- names, order and ignored properties ----

    private class Base
    {
        public int BaseId { get; set; }

        public string? BaseNote { get; set; }
    }

    private sealed class Derived : Base
    {
        public int Second { get; set; }

        [ParquetColumn("renamed")]
        public int Third { get; set; }

        [ParquetColumn(Order = -1)]
        public int First { get; set; }

        [ParquetColumn("late", Order = 5)]
        public int Last { get; set; }

        [ParquetIgnore]
        public int Ignored { get; set; }

        public int ReadOnly => 42;

        public int WriteOnlyBackingField { private get; set; }

        private int Hidden { get; set; }

        internal int Internal { get; set; }

        public static int Static { get; set; }

        public int this[int index] => index;
    }

    [Test]
    public async Task TheColumns_FollowTheOrder_ThenBaseTypesFirst_ThenDeclaration()
    {
        var names = ParquetSerializer.GetSchema<Derived>().Columns.Select(c => c.Name).ToArray();

        // Order -1 first, then order 0: the base class, then the derived class in declaration order, then order 5.
        // The write-only property has no public getter and the indexer and statics are not properties of a row.
        await Assert.That(names).IsEquivalentTo(new[] { "First", "BaseId", "BaseNote", "Second", "renamed", "ReadOnly", "late" });
        await Assert.That(names.SequenceEqual(new[] { "First", "BaseId", "BaseNote", "Second", "renamed", "ReadOnly", "late" })).IsTrue();
    }

    [Test]
    public async Task ReadOnlyAndInheritedProperties_AreWritten()
    {
        var bytes = Serialize([new Derived { BaseId = 7, BaseNote = "n", Second = 2, Third = 3, First = 1, Last = 9 }]);

        await Assert.That(SerializedColumns.Read(bytes, "ReadOnly")).IsEquivalentTo(new object?[] { 42 });
        await Assert.That(SerializedColumns.Read(bytes, "BaseId")).IsEquivalentTo(new object?[] { 7 });
        await Assert.That(SerializedColumns.Read(bytes, "renamed")).IsEquivalentTo(new object?[] { 3 });
        await Assert.That(SerializedColumns.Read(bytes, "late")).IsEquivalentTo(new object?[] { 9 });
    }

    // ---- rows that are records and structs ----

    private sealed record Person(string Name, int Age, string? Email);

    private record struct Point(double X, double Y);

    private readonly struct Reading
    {
        public Reading(int sensor, decimal? value)
        {
            Sensor = sensor;
            Value = value;
        }

        public int Sensor { get; }

        [ParquetDecimal(10, 3)]
        public decimal? Value { get; }
    }

    [Test]
    public async Task Records_AndStructs_AreRows()
    {
        var people = Serialize([new Person("Ann", 30, null), new Person("Bob", 41, "bob@example.com")]);
        var points = Serialize([new Point(1.5, -2), new Point(0, 4)]);
        var readings = Serialize([new Reading(1, 1.5m), new Reading(2, null), new Reading(3, -0.001m)]);

        await Assert.That(SerializedColumns.Read(people, "Name")).IsEquivalentTo(new object?[] { "Ann", "Bob" });
        await Assert.That(SerializedColumns.Read(people, "Email")).IsEquivalentTo(new object?[] { null, "bob@example.com" });
        await Assert.That(SerializedColumns.Read(points, "X")).IsEquivalentTo(new object?[] { 1.5, 0.0 });
        await Assert.That(SerializedColumns.Read(points, "Y")).IsEquivalentTo(new object?[] { -2.0, 4.0 });
        await Assert.That(SerializedColumns.Read(readings, "Value")).IsEquivalentTo(new object?[] { 1.5m, null, -0.001m });
        await Assert.That(ParquetSerializer.GetSchema<Person>().Columns.Select(c => c.Repetition)).IsEquivalentTo(
            new[] { FieldRepetitionType.Required, FieldRepetitionType.Required, FieldRepetitionType.Optional });
    }

#nullable disable
    private sealed class Oblivious
    {
        public string Text { get; set; }

        public byte[] Bytes { get; set; }

        public int Number { get; set; }
    }
#nullable restore

    [Test]
    public async Task ReferenceTypes_FromCodeWithoutNullableAnnotations_AreOptional()
    {
        // Nothing says they are never null there, so they may be.
        var schema = ParquetSerializer.GetSchema<Oblivious>();

        await Assert.That(schema.Columns.Select(c => c.Repetition)).IsEquivalentTo(
            new[] { FieldRepetitionType.Optional, FieldRepetitionType.Optional, FieldRepetitionType.Required });
        var bytes = Serialize([new Oblivious { Text = null, Bytes = [1], Number = 1 }]);
        await Assert.That(SerializedColumns.Read(bytes, "Text")).IsEquivalentTo(new object?[] { null });
    }

    // ---- the attributes ----

    private sealed class Tuned
    {
        [ParquetTimestamp(TimeUnit.Millis)]
        public DateTime Millis { get; set; }

        [ParquetTimestamp(TimeUnit.Nanos, IsAdjustedToUtc = false)]
        public DateTime LocalNanos { get; set; }

        [ParquetTimestamp(TimeUnit.Millis)]
        public DateTimeOffset OffsetMillis { get; set; }

        [ParquetTime(TimeUnit.Millis)]
        public TimeOnly TimeMillis { get; set; }

        [ParquetTime(TimeUnit.Nanos, IsAdjustedToUtc = true)]
        public TimeOnly TimeNanos { get; set; }

        [ParquetDecimal(5, 0)]
        public decimal Tiny { get; set; }

        [ParquetDecimal(9, 4)]
        public decimal Nine { get; set; }

        [ParquetDecimal(10, 4)]
        public decimal Ten { get; set; }

        [ParquetDecimal(18, 6)]
        public decimal Eighteen { get; set; }

        [ParquetDecimal(19, 6)]
        public decimal Nineteen { get; set; }

        [ParquetDecimal(28, 28)]
        public decimal Fraction { get; set; }

        [ParquetDecimal(31, 3)]
        public decimal Thirty { get; set; }

        [ParquetEncoding(ParquetEncoding.DeltaBinaryPacked)]
        public int Steady { get; set; }

        [ParquetEncoding(ParquetEncoding.ByteStreamSplit)]
        public double Measured { get; set; }
    }

    [Test]
    public async Task TheAttributes_AdjustTheColumns()
    {
        var columns = ParquetSerializer.GetSchema<Tuned>().Columns.ToDictionary(c => c.Name);

        await Assert.That(columns["Millis"].LogicalType).IsEqualTo(LogicalType.Timestamp(true, TimeUnit.Millis));
        await Assert.That(columns["LocalNanos"].LogicalType).IsEqualTo(LogicalType.Timestamp(false, TimeUnit.Nanos));
        await Assert.That(columns["OffsetMillis"].LogicalType).IsEqualTo(LogicalType.Timestamp(true, TimeUnit.Millis));
        await Assert.That(columns["TimeMillis"].Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(columns["TimeMillis"].LogicalType).IsEqualTo(LogicalType.Time(false, TimeUnit.Millis));
        await Assert.That(columns["TimeNanos"].Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(columns["TimeNanos"].LogicalType).IsEqualTo(LogicalType.Time(true, TimeUnit.Nanos));

        // The physical type of a decimal follows its precision: 9 digits fit an INT32, 18 an INT64, then the fewest bytes that hold them.
        await Assert.That(columns["Tiny"].Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(columns["Nine"].Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(columns["Ten"].Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(columns["Eighteen"].Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(columns["Nineteen"].Type).IsEqualTo(PhysicalType.FixedLenByteArray);
        await Assert.That(columns["Nineteen"].TypeLength).IsEqualTo(9);
        await Assert.That(columns["Thirty"].TypeLength).IsEqualTo(13);
        await Assert.That(columns["Fraction"].LogicalType).IsEqualTo(LogicalType.Decimal(28, 28));
    }

    [Test]
    public async Task TheTunedColumns_RoundTrip()
    {
        var row = new Tuned
        {
            Millis = new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc),
            LocalNanos = new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Unspecified).AddTicks(4567),
            OffsetMillis = new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.FromHours(-5)),
            TimeMillis = new TimeOnly(13, 14, 15, 678),
            TimeNanos = new TimeOnly(1, 2, 3, 4),
            Tiny = 12345m,
            Nine = 12345.6789m,
            Ten = -123456.7891m,
            Eighteen = 123456789012.345678m,
            Nineteen = 1234567890123.456789m,
            Fraction = 0.1234567890123456789012345678m,
            Thirty = 12345678901234567890123456.789m,
            Steady = 5,
            Measured = Math.PI,
        };

        var bytes = Serialize([row, row]);

        await Assert.That(SerializedColumns.Read(bytes, "Millis")[1]).IsEqualTo(row.Millis);
        await Assert.That(SerializedColumns.Read(bytes, "LocalNanos")[1]).IsEqualTo(row.LocalNanos);
        await Assert.That(SerializedColumns.Read(bytes, "OffsetMillis")[1]).IsEqualTo(row.OffsetMillis.UtcDateTime);
        await Assert.That(SerializedColumns.Read(bytes, "TimeMillis")[1]).IsEqualTo(row.TimeMillis);
        await Assert.That(SerializedColumns.Read(bytes, "TimeNanos")[1]).IsEqualTo(row.TimeNanos);
        foreach (string name in new[] { "Tiny", "Nine", "Ten", "Eighteen", "Nineteen", "Fraction", "Thirty" })
        {
            await Assert.That(SerializedColumns.Read(bytes, name)[1]).IsEqualTo(typeof(Tuned).GetProperty(name)!.GetValue(row)).Because(name);
        }
    }

    [Test]
    public async Task AnEncodingAttribute_ChoosesTheColumnsEncoding_AndTheOptionsWin()
    {
        var rows = Enumerable.Range(0, 300).Select(i => new Tuned { LocalNanos = DateTime.UnixEpoch, Steady = i * 3, Measured = i / 8.0, Tiny = 1, Nine = 1, Ten = 1, Eighteen = 1, Nineteen = 1, Fraction = 0, Thirty = 1 }).ToList();
        var noDictionary = new ParquetWriterOptions { DictionaryEnabled = false };

        var tuned = TestParquetFile.Parse(Serialize(rows, new ParquetSerializerOptions { Writer = noDictionary }));
        var overridden = TestParquetFile.Parse(Serialize(rows, new ParquetSerializerOptions
        {
            Writer = noDictionary with { ColumnEncodings = new Dictionary<string, ParquetEncoding> { ["Steady"] = ParquetEncoding.Plain } },
        }));

        int steady = tuned.Schema.Columns.ToList().FindIndex(c => c.Name == "Steady");
        int measured = tuned.Schema.Columns.ToList().FindIndex(c => c.Name == "Measured");
        await Assert.That(tuned.Metadata.RowGroups[0].Columns[steady].MetaData!.Encodings).Contains(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(tuned.Metadata.RowGroups[0].Columns[measured].MetaData!.Encodings).Contains(ParquetEncoding.ByteStreamSplit);
        await Assert.That(overridden.Metadata.RowGroups[0].Columns[steady].MetaData!.Encodings).DoesNotContain(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(overridden.Metadata.RowGroups[0].Columns[measured].MetaData!.Encodings).Contains(ParquetEncoding.ByteStreamSplit);
    }

    [Test]
    public async Task AnEncodingTheColumnCannotUse_IsReported()
    {
        await Assert.That(() => Serialize([new WrongEncoding()])).Throws<ArgumentException>();
    }

    private sealed class WrongEncoding
    {
        [ParquetEncoding(ParquetEncoding.DeltaBinaryPacked)]
        public string Name { get; set; } = "";
    }

    // ---- models that cannot be written ----

    private sealed class Unsupported
    {
        public int Id { get; set; }

        public TimeSpan Duration { get; set; }
    }

    private sealed class Clash
    {
        public int A { get; set; }

        [ParquetColumn("A")]
        public int B { get; set; }
    }

    private sealed class Empty
    {
        [ParquetIgnore]
        public int Hidden { get; set; }
    }

    private sealed class WrongDecimal
    {
        [ParquetDecimal(5, 2)]
        public int NotADecimal { get; set; }
    }

    private sealed class WrongTimestamp
    {
        [ParquetTimestamp]
        public DateOnly NotATimestamp { get; set; }
    }

    private sealed class WrongTime
    {
        [ParquetTime]
        public DateTime NotATime { get; set; }
    }

    [Test]
    public async Task APropertyOfAnUnsupportedType_IsReported_WithItsName()
    {
        var exception = Assert.Throws<NotSupportedException>(() => ParquetSerializer.GetSchema<Unsupported>());

        await Assert.That(exception.Message).Contains("Unsupported.Duration");
        await Assert.That(exception.Message).Contains("TimeSpan");
        await Assert.That(() => ParquetSerializer.GetSchema<int>()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ABadModel_IsReported_EachTimeItIsUsed()
    {
        await Assert.That(() => ParquetSerializer.GetSchema<Clash>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<Clash>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<Empty>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WrongDecimal>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WrongTimestamp>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WrongTime>()).Throws<InvalidOperationException>();
        await Assert.That(() => Serialize([new Clash()])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheAttributeArguments_AreChecked()
    {
        await Assert.That(() => new ParquetDecimalAttribute(0, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetDecimalAttribute(39, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetDecimalAttribute(10, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetDecimalAttribute(10, 11)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetDecimalAttribute(38, 29)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new ParquetDecimalAttribute(38, 28).Scale).IsEqualTo(28);
        await Assert.That(() => new ParquetTimestampAttribute((TimeUnit)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetTimeAttribute((TimeUnit)9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ParquetColumnAttribute("")).Throws<ArgumentException>();
        await Assert.That(new ParquetColumnAttribute().Name).IsNull();
        await Assert.That(new ParquetTimestampAttribute().Unit).IsEqualTo(TimeUnit.Micros);
        await Assert.That(new ParquetTimestampAttribute().IsAdjustedToUtc).IsTrue();
        await Assert.That(new ParquetTimeAttribute().IsAdjustedToUtc).IsFalse();
    }
}
