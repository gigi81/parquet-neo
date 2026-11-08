using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Serialization.Columns;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class DeserializerCompatibilityTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    private static List<TRow> Read<TRow>(byte[] bytes) => ParquetSerializer.Deserialize<TRow>(new MemoryStream(bytes));

    // ---- which columns can be read as which types ----

    private sealed class IntFromShort
    {
        [ParquetColumn("Short")]
        public int Value { get; set; }
    }

    private sealed class IntFromTiny
    {
        [ParquetColumn("Tiny")]
        public int Value { get; set; }
    }

    private sealed class LongFromInt
    {
        [ParquetColumn("Int")]
        public long Value { get; set; }
    }

    private sealed class IntFromUnsigned
    {
        [ParquetColumn("UInt")]
        public int Value { get; set; }
    }

    private sealed class UIntFromShortUnsigned
    {
        [ParquetColumn("UShort")]
        public uint Value { get; set; }
    }

    private sealed class LongFromUnsigned
    {
        [ParquetColumn("ULong")]
        public long Value { get; set; }
    }

    private sealed class BytesFromText
    {
        [ParquetColumn("Name")]
        public byte[] Value { get; set; } = [];
    }

    private sealed class BytesFromBytes
    {
        [ParquetColumn("Data")]
        public byte[] Value { get; set; } = [];
    }

    private sealed class TimeFromNumber
    {
        [ParquetColumn("Int")]
        public TimeOnly Value { get; set; }
    }

    private sealed class GuidFromHalf
    {
        [ParquetColumn("Half")]
        public Guid Value { get; set; }
    }

    private sealed class DateFromNumber
    {
        [ParquetColumn("Int")]
        public DateOnly Value { get; set; }
    }

    [Test]
    public async Task SignedIntegersOfAnyWidth_CanBeReadAsInt()
    {
        var bytes = Serialize(Sample.Rows(5));

        await Assert.That(Read<IntFromShort>(bytes).Select(r => r.Value)).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => i * 1000 - 30000));
        await Assert.That(Read<IntFromTiny>(bytes).Select(r => r.Value)).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => i - 100));
        await Assert.That(Read<BytesFromBytes>(bytes).Select(r => r.Value.Length)).IsEquivalentTo(new[] { 2, 2, 2, 2, 2 });
    }

    [Test]
    public async Task AColumnThatTheTypeCouldMisreadOrOnlyPartlyHold_IsRefused()
    {
        var bytes = Serialize(Sample.Rows(5));
        Action[] reads =
        [
            () => Read<LongFromInt>(bytes),
            () => Read<IntFromUnsigned>(bytes),
            () => Read<UIntFromShortUnsigned>(bytes),
            () => Read<LongFromUnsigned>(bytes),
            () => Read<BytesFromText>(bytes),
            () => Read<TimeFromNumber>(bytes),
            () => Read<GuidFromHalf>(bytes),
            () => Read<DateFromNumber>(bytes),
        ];

        for (int i = 0; i < reads.Length; i++)
        {
            await Assert.That(reads[i]).Throws<SchemaMismatchException>().Because($"case {i}");
        }
    }

    private sealed class DecimalRow
    {
        [ParquetColumn("d")]
        public decimal Value { get; set; }
    }

    [Test]
    public async Task ADecimalWithAScaleAbove28_CannotBeRead()
    {
        var stream = new MemoryStream();
        var node = new PrimitiveNode("d", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, LogicalType.Decimal(29, 38), typeLength: 16);
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteFixedLengthByteArrays(new byte[16]);
        }

        var exception = Assert.Throws<SchemaMismatchException>(() => Read<DecimalRow>(stream.ToArray()));

        await Assert.That(exception.Message).Contains("DECIMAL(38, 29)");
    }

    private sealed class FromRepeated
    {
        [ParquetColumn("x")]
        public int Value { get; set; }
    }

    [Test]
    public async Task ARepeatedColumn_CannotBeReadAsASimpleType()
    {
        var stream = new MemoryStream();
        var node = new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Repeated);
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2, 3], [1, 1, 1], [0, 1, 0]);
        }

        var exception = Assert.Throws<SchemaMismatchException>(() => Read<FromRepeated>(stream.ToArray()));

        await Assert.That(exception.Message).Contains("repeated");
    }

    // ---- constructors ----

    private sealed class TwoConstructors
    {
        public TwoConstructors(int id)
        {
            Id = id;
            Name = "from the short constructor";
        }

        public TwoConstructors(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }
    }

    private sealed class MismatchedConstructor
    {
        public MismatchedConstructor(string id)
        {
            Id = id.Length;
        }

        public int Id { get; }
    }

    private sealed class Pair
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    [Test]
    public async Task TheConstructorWithTheMostParameters_IsUsed()
    {
        var rows = Read<TwoConstructors>(Serialize([new Pair { Id = 1, Name = "one" }]));

        await Assert.That(rows[0].Id).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("one");
    }

    [Test]
    public async Task AConstructorParameterOfAnotherTypeThanTheProperty_IsNotUsed()
    {
        await Assert.That(() => Read<MismatchedConstructor>(Serialize([new Pair { Id = 1, Name = "one" }]))).Throws<InvalidOperationException>();
    }

    // ---- more than one batch of values ----

    private sealed class Optionals
    {
        public string? Nickname { get; set; }

        public int? OptionalInt { get; set; }

        public double? OptionalDouble { get; set; }
    }

    [Test]
    public async Task ColumnsWithNulls_AreReadAcrossSeveralBatches()
    {
        var rows = Sample.Rows(20_000);
        var bytes = Serialize(rows);

        var read = Read<Optionals>(bytes);
        var asynchronous = await ParquetSerializer.DeserializeAsync<Optionals>(new MemoryStream(bytes));

        await Assert.That(read.Count).IsEqualTo(20_000);
        for (int i = 0; i < rows.Count; i += 7)
        {
            await Assert.That(read[i].Nickname).IsEqualTo(rows[i].Nickname).Because($"row {i}");
            await Assert.That(read[i].OptionalInt).IsEqualTo(rows[i].OptionalInt).Because($"row {i}");
            await Assert.That(asynchronous[i].OptionalDouble).IsEqualTo(rows[i].OptionalDouble).Because($"row {i}");
        }

        await Assert.That(read.Select(r => r.OptionalInt).SequenceEqual(rows.Select(r => r.OptionalInt))).IsTrue();
        await Assert.That(read.Select(r => r.Nickname).SequenceEqual(rows.Select(r => r.Nickname))).IsTrue();
    }

    [Test]
    public async Task AnEntryBeyondTheColumn_IsReported()
    {
        var bytes = Serialize(Sample.Rows(3));
        using var reader = ReadHelper.Open(bytes);
        using var columnReader = reader.OpenRowGroup(0).OpenColumn("Int");
        var column = new LeafColumn<int>(ColumnTypeRegistry.Get<int>());
        column.Fill(columnReader);

        for (int i = 0; i < 3; i++)
        {
            await Assert.That(column.TryTake(out int value)).IsTrue();
            await Assert.That(value).IsEqualTo(i * 100_000 - 2_000_000);
        }

        await Assert.That(() => column.TryTake(out _)).Throws<CorruptColumnException>();

        // A column the file does not have is all nulls, however many entries are taken.
        var missing = new LeafColumn<int>(ColumnTypeRegistry.Get<int>());
        missing.Fill(null);
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(missing.TryTake(out _)).IsFalse();
        }

        await Assert.That(missing.Definition).IsEqualTo(0);
        await Assert.That(missing.Repetition).IsEqualTo(0);
        await Assert.That(missing.AtEnd).IsTrue();
        missing.Skip();
    }

    // ---- enum names ----

    private enum Color
    {
        Red,
        Green,
    }

    private sealed class JustColor
    {
        public Color Color { get; set; }
    }

    private sealed class Texts
    {
        public string Color { get; set; } = "";
    }

    [Test]
    public async Task EnumNames_AreCaseSensitive()
    {
        await Assert.That(Read<JustColor>(Serialize([new Texts { Color = "Green" }]))[0].Color).IsEqualTo(Color.Green);
        await Assert.That(() => Read<JustColor>(Serialize([new Texts { Color = "green" }]))).Throws<ValueConversionException>();
    }

    // ---- ranges read ahead ----

    [Test]
    public async Task OverlappingAndTouchingRanges_AreJoined()
    {
        var merged = DeserializationSession<Sample>.Merge([(0, 100), (10, 20), (100, 5), (200, 10), (205, 3), (300, 1)]);

        await Assert.That(merged).IsEquivalentTo(new (long, int)[] { (0, 105), (200, 10), (300, 1) });
        await Assert.That(DeserializationSession<Sample>.Merge([(50, 10), (0, 10)])).IsEquivalentTo(new (long, int)[] { (0, 10), (50, 10) });
        await Assert.That(DeserializationSession<Sample>.Merge([])).IsEmpty();
    }

    [Test]
    public async Task APrefetchedStream_ReleasesTheChunksItDrops()
    {
        var source = new MemoryStream(new byte[100]);
        using var stream = new PrefetchedStream(source.Length);

        await stream.LoadKeptAsync(source, 0, 4, CancellationToken.None);
        await stream.LoadChunkAsync(source, 10, 30, CancellationToken.None);
        await stream.LoadChunkAsync(source, 50, 20, CancellationToken.None);
        await Assert.That(stream.LoadedBytes).IsEqualTo(50L);
        stream.DropLoaded();

        await Assert.That(stream.LoadedBytes).IsEqualTo(0L);
    }

    // ---- row counts and cancellation ----

    [Test]
    public async Task ARowGroupWithMoreRowsThanAListHolds_IsRefused()
    {
        var bytes = FooterEditor.Rewrite(Serialize(Sample.Rows(5)), f => f.RowGroups[0].NumRows = int.MaxValue + 1L);

        await Assert.That(() => Read<IntAndName>(bytes)).Throws<NotSupportedException>();
    }

    private sealed class IntAndName
    {
        public int Int { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class IgnoresCancellation(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer, CancellationToken.None);
    }

    [Test]
    public async Task Cancelling_BetweenRowGroups_StopsTheReading_EvenForAStreamThatIgnoresTheToken()
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(Sample.Rows(300), stream, new ParquetSerializerOptions { RowGroupSize = 100 });
        using var source = new CancellationTokenSource();
        var rows = 0;

        await Assert.That(async () =>
        {
            await foreach (var row in ParquetSerializer.DeserializeAllAsync<IntAndName>(new IgnoresCancellation(stream.ToArray()), null, source.Token))
            {
                if (++rows == 100)
                {
                    await source.CancelAsync();
                }
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(rows).IsEqualTo(100);
    }
}
