using System.Text;
using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Core.Stats;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Stats;

public class StatisticsWriterTests
{
    /// <summary>The bounds the sample data has over rows [from, from + count), computed straight from its formulas.</summary>
    internal static (object? Min, object? Max, long Nulls) Expected(bool optional, int from, int count, int column)
    {
        var rows = Enumerable.Range(from, count).ToList();
        long nulls = optional ? rows.Count(i => SampleFile.IsNull(column, i)) : 0;
        var values = rows.Where(i => !optional || !SampleFile.IsNull(column, i)).Select(i => SampleFile.Value(column, i)).ToList();
        if (values.Count == 0)
        {
            return (null, null, nulls);
        }

        Comparison<object> compare = column switch
        {
            4 => (a, b) => ((bool)a).CompareTo((bool)b),
            0 or 8 or 11 => (a, b) => ((int)a).CompareTo((int)b),
            1 or 9 => (a, b) => ((long)a).CompareTo((long)b),
            12 => (a, b) => unchecked((ulong)(long)a).CompareTo(unchecked((ulong)(long)b)),
            2 => (a, b) => ((float)a).CompareTo((float)b),
            3 => (a, b) => ((double)a).CompareTo((double)b),
            _ => (a, b) => ((byte[])a).AsSpan().SequenceCompareTo((byte[])b), // 5, 6, 7 and 10 (positive decimals)
        };
        return (values.MinBy(v => v, Comparer<object>.Create(compare)), values.MaxBy(v => v, Comparer<object>.Create(compare)), nulls);
    }

    [Test]
    [Arguments(false, true, CompressionCodec.Uncompressed, 1024 * 1024)]
    [Arguments(true, true, CompressionCodec.Uncompressed, 1024 * 1024)]
    [Arguments(true, false, CompressionCodec.Uncompressed, 500)]
    [Arguments(true, true, CompressionCodec.Snappy, 700)]
    [Arguments(false, false, CompressionCodec.Zstd, 900)]
    public async Task TheSampleDataset_HasExactStatistics_PerRowGroup(bool optional, bool dictionary, CompressionCodec codec, int pageSize)
    {
        var options = new ParquetWriterOptions { Compression = codec, Codecs = TestParquetFile.Registry, DataPageSize = pageSize, DictionaryEnabled = dictionary };
        var bytes = SampleFile.Build(optional, 1500, groups: 3, options: options);
        using var reader = ReadHelper.Open(bytes);

        for (int group = 0; group < 3; group++)
        {
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                var statistics = reader.OpenRowGroup(group).GetStatistics(column)!;
                var (min, max, nulls) = Expected(optional, group * 500, 500, column);

                string where = $"group {group} {SampleFile.ColumnNames[column]}";
                await Assert.That(SampleFile.Canonical(statistics.Min)).IsEqualTo(SampleFile.Canonical(min)).Because(where);
                await Assert.That(SampleFile.Canonical(statistics.Max)).IsEqualTo(SampleFile.Canonical(max)).Because(where);
                await Assert.That(statistics.NullCount).IsEqualTo(nulls).Because(where);
                await Assert.That(statistics.IsMinExact).IsTrue().Because(where);
                await Assert.That(statistics.IsMaxExact).IsTrue().Because(where);
            }
        }
    }

    [Test]
    public async Task FloatingPointColumns_HaveANaNCount_AndOtherColumnsDoNot()
    {
        var bytes = SampleFile.Build(optional: true, rows: 300);
        var file = TestParquetFile.Parse(bytes);

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var statistics = file.Chunk(0, column).Statistics!;
            bool floating = column is 2 or 3;

            await Assert.That(statistics.NanCount is not null).IsEqualTo(floating).Because(SampleFile.ColumnNames[column]);
        }
    }

    [Test]
    public async Task TheRawStatistics_UseOnlyTheCurrentFields()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: false, rows: 100));

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var statistics = file.Chunk(0, column).Statistics!;

            // The deprecated min/max fields were written with the wrong order for many types and are not produced.
            await Assert.That(statistics.Min).IsNull();
            await Assert.That(statistics.Max).IsNull();
            await Assert.That(statistics.DistinctCount).IsNull();
            await Assert.That(statistics.MinValue).IsNotNull();
            await Assert.That(statistics.NullCount).IsEqualTo(0L).Because("a required column still states a null count of zero");
        }
    }

    [Test]
    public async Task StatisticsCanBeTurnedOff()
    {
        var bytes = SampleFile.Build(optional: true, rows: 300, options: new ParquetWriterOptions { StatisticsEnabled = false });
        var file = TestParquetFile.Parse(bytes);
        using var reader = ReadHelper.Open(bytes);

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            await Assert.That(file.Chunk(0, column).Statistics).IsNull();
            await Assert.That(reader.OpenRowGroup(0).GetStatistics(column)).IsNull();
        }
    }

    [Test]
    public async Task TheStatistics_DoNotDependOnTheEncoding()
    {
        // Plain pages, dictionary pages and a dictionary that falls back to plain all see the same values.
        var plain = SampleFile.Build(true, 3000, options: new ParquetWriterOptions { DictionaryEnabled = false, DataPageSize = 600 });
        var dictionary = SampleFile.Build(true, 3000, options: new ParquetWriterOptions { DataPageSize = 600 });
        var fallback = SampleFile.Build(true, 3000, options: new ParquetWriterOptions { DataPageSize = 600, DictionaryPageSizeLimit = 100 });

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            string Dump(byte[] file)
            {
                var s = TestParquetFile.Parse(file).Chunk(0, column).Statistics!;
                return $"{SampleFile.Canonical(s.MinValue)} {SampleFile.Canonical(s.MaxValue)} {s.NullCount} {s.NanCount} {s.IsMinValueExact} {s.IsMaxValueExact}";
            }

            await Assert.That(Dump(dictionary)).IsEqualTo(Dump(plain)).Because(SampleFile.ColumnNames[column]);
            await Assert.That(Dump(fallback)).IsEqualTo(Dump(plain)).Because(SampleFile.ColumnNames[column]);
        }
    }

    // ---- single columns ----

    private static byte[] WriteOne(PrimitiveNode column, Action<ColumnWriter> write, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([column]), options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            write(group.NextColumn());
        }

        return stream.ToArray();
    }

    private static ColumnStatistics Stats(byte[] bytes)
    {
        using var reader = ReadHelper.Open(bytes);
        return reader.OpenRowGroup(0).GetStatistics(0)!;
    }

    [Test]
    public async Task AnAllNullColumn_HasANullCountAndNoBounds()
    {
        var bytes = WriteOne(new PrimitiveNode("n", PhysicalType.Int64, FieldRepetitionType.Optional), w => w.WriteBatch(ReadOnlySpan<long>.Empty, new int[250]));
        var raw = TestParquetFile.Parse(bytes).Chunk(0, 0).Statistics!;

        await Assert.That(raw.NullCount).IsEqualTo(250L);
        await Assert.That(raw.MinValue).IsNull();
        await Assert.That(raw.MaxValue).IsNull();
        await Assert.That(raw.IsMinValueExact).IsNull();
    }

    [Test]
    public async Task ValuesInManyBatches_AllCount()
    {
        var bytes = WriteOne(new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Optional), w =>
        {
            w.WriteBatch([50, 60], [1, 0, 1]);
            w.WriteBatch([-7], [0, 0, 1]);
            w.WriteBatch([900, 1], [1, 1]);
            w.WriteBatch(ReadOnlySpan<int>.Empty, [0]);
        });

        var statistics = Stats(bytes);

        await Assert.That(statistics.Min).IsEqualTo(-7);
        await Assert.That(statistics.Max).IsEqualTo(900);
        await Assert.That(statistics.NullCount).IsEqualTo(4L);
    }

    [Test]
    public async Task UnsignedIntegers_AreOrderedUnsigned()
    {
        var u32 = WriteOne(new PrimitiveNode("u", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(32, false)), w => w.WriteBatch([5, -1, 7, int.MinValue]));
        var u64 = WriteOne(new PrimitiveNode("u", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Integer(64, false)), w => w.WriteBatch([5L, -1L, 7L, long.MinValue]));
        var u8 = WriteOne(new PrimitiveNode("u", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(8, false)), w => w.WriteBatch([200, 5, 255, 0]));
        var signed = WriteOne(new PrimitiveNode("s", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(32, true)), w => w.WriteBatch([5, -1, 7, int.MinValue]));

        await Assert.That(Stats(u32).Min).IsEqualTo(5);
        await Assert.That(Stats(u32).Max).IsEqualTo(-1); // 0xFFFFFFFF
        await Assert.That(Stats(u64).Min).IsEqualTo(5L);
        await Assert.That(Stats(u64).Max).IsEqualTo(-1L);
        await Assert.That(Stats(u8).Min).IsEqualTo(0);
        await Assert.That(Stats(u8).Max).IsEqualTo(255);
        await Assert.That(Stats(signed).Min).IsEqualTo(int.MinValue);
        await Assert.That(Stats(signed).Max).IsEqualTo(7);
    }

    [Test]
    public async Task IntegerExtremes_AreBounds()
    {
        var ints = WriteOne(new PrimitiveNode("i", PhysicalType.Int32, FieldRepetitionType.Required), w => w.WriteBatch([int.MaxValue, 0, int.MinValue]));
        var longs = WriteOne(new PrimitiveNode("i", PhysicalType.Int64, FieldRepetitionType.Required), w => w.WriteBatch([long.MaxValue, 0, long.MinValue]));

        await Assert.That(Stats(ints).Min).IsEqualTo(int.MinValue);
        await Assert.That(Stats(ints).Max).IsEqualTo(int.MaxValue);
        await Assert.That(Stats(longs).Min).IsEqualTo(long.MinValue);
        await Assert.That(Stats(longs).Max).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task FloatingPoint_FollowsTheNaNAndZeroRules()
    {
        var bytes = WriteOne(new PrimitiveNode("d", PhysicalType.Double, FieldRepetitionType.Required), w => w.WriteBatch([double.NaN, 0.0, -0.0, 2.5, double.NaN]));
        var statistics = Stats(bytes);

        await Assert.That(BitConverter.DoubleToInt64Bits((double)statistics.Min!)).IsEqualTo(BitConverter.DoubleToInt64Bits(-0.0));
        await Assert.That(statistics.Max).IsEqualTo(2.5);
        await Assert.That(statistics.NanCount).IsEqualTo(2L);

        var allNaN = Stats(WriteOne(new PrimitiveNode("d", PhysicalType.Double, FieldRepetitionType.Required), w => w.WriteBatch([double.NaN, double.NaN])));
        await Assert.That(allNaN.Min).IsNull();
        await Assert.That(allNaN.Max).IsNull();
        await Assert.That(allNaN.NanCount).IsEqualTo(2L);
    }

    [Test]
    public async Task Booleans_HaveBounds_AndNullCounts()
    {
        var bytes = WriteOne(new PrimitiveNode("b", PhysicalType.Boolean, FieldRepetitionType.Optional), w => w.WriteBatch([true, true], [1, 0, 1, 0]));
        var statistics = Stats(bytes);

        await Assert.That(statistics.Min is true).IsTrue();
        await Assert.That(statistics.Max is true).IsTrue();
        await Assert.That(statistics.NullCount).IsEqualTo(2L);

        var mixed = Stats(WriteOne(new PrimitiveNode("b", PhysicalType.Boolean, FieldRepetitionType.Required), w => w.WriteBatch([true, false])));
        await Assert.That(mixed.Min is false).IsTrue();
        await Assert.That(mixed.Max is true).IsTrue();
    }

    [Test]
    public async Task Strings_AreOrderedAsUnsignedUtf8Bytes()
    {
        var bytes = WriteOne(new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String), w => w.WriteStrings(["z", "é", "Z", "", "apple"], [1, 1, 0, 1, 1, 1]));
        var statistics = Stats(bytes);

        await Assert.That(Encoding.UTF8.GetString((byte[])statistics.Min!)).IsEqualTo("");
        await Assert.That(Encoding.UTF8.GetString((byte[])statistics.Max!)).IsEqualTo("é");
        await Assert.That(statistics.NullCount).IsEqualTo(1L);
    }

    [Test]
    public async Task Strings_GiveTheSameStatistics_WithOrWithoutADictionary()
    {
        string[] values = Enumerable.Range(0, 400).Select(i => "key-" + (i * 37 % 101)).ToArray();
        static PrimitiveNode Node() => new("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);

        var plain = Stats(WriteOne(Node(), w => w.WriteStrings(values), new ParquetWriterOptions { DictionaryEnabled = false }));
        var dictionary = Stats(WriteOne(Node(), w => w.WriteStrings(values)));

        await Assert.That(Encoding.UTF8.GetString((byte[])plain.Min!)).IsEqualTo("key-0");
        await Assert.That(Encoding.UTF8.GetString((byte[])plain.Max!)).IsEqualTo("key-99");
        await Assert.That(Encoding.UTF8.GetString((byte[])dictionary.Max!)).IsEqualTo("key-99");
    }

    [Test]
    public async Task ByteArrays_AndFixedLengthArrays_AreOrderedAsUnsignedBytes()
    {
        var bytes = WriteOne(new PrimitiveNode("b", PhysicalType.ByteArray, FieldRepetitionType.Required), w => w.WriteByteArrays([new byte[] { 0x7F }, new byte[] { 0x80 }, new byte[] { 0x01, 0x02 }]));
        var fixedBytes = WriteOne(new PrimitiveNode("f", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, typeLength: 2), w => w.WriteFixedLengthByteArrays([0x7F, 0x00, 0x80, 0x00, 0x00, 0xFF]));

        await Assert.That((byte[])Stats(bytes).Min!).IsEquivalentTo(new byte[] { 0x01, 0x02 });
        await Assert.That((byte[])Stats(bytes).Max!).IsEquivalentTo(new byte[] { 0x80 });
        await Assert.That((byte[])Stats(fixedBytes).Min!).IsEquivalentTo(new byte[] { 0x00, 0xFF });
        await Assert.That((byte[])Stats(fixedBytes).Max!).IsEquivalentTo(new byte[] { 0x80, 0x00 });
    }

    [Test]
    public async Task Decimals_AreOrderedByTheirValue()
    {
        // 5-byte two's complement numbers: -300, 12, 0, -1.
        byte[] values =
        [
            0xFF, 0xFF, 0xFF, 0xFE, 0xD4,
            0x00, 0x00, 0x00, 0x00, 0x0C,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        ];
        var bytes = WriteOne(new PrimitiveNode("d", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, LogicalType.Decimal(2, 10), typeLength: 5), w => w.WriteFixedLengthByteArrays(values));
        var statistics = Stats(bytes);

        await Assert.That((byte[])statistics.Min!).IsEquivalentTo(values[..5]);
        await Assert.That((byte[])statistics.Max!).IsEquivalentTo(values[5..10]);
    }

    [Test]
    public async Task Float16_HasOnlyANullCount()
    {
        var bytes = WriteOne(new PrimitiveNode("h", PhysicalType.FixedLenByteArray, FieldRepetitionType.Optional, LogicalType.Float16, typeLength: 2), w => w.WriteFixedLengthByteArrays([0x00, 0x3C, 0x00, 0x40], [1, 0, 1]));
        var raw = TestParquetFile.Parse(bytes).Chunk(0, 0).Statistics!;

        await Assert.That(raw.NullCount).IsEqualTo(1L);
        await Assert.That(raw.MinValue).IsNull();
        await Assert.That(raw.MaxValue).IsNull();
        await Assert.That(raw.NanCount).IsNull();
    }

    // ---- long values ----

    [Test]
    public async Task LongStrings_GetShortenedBounds_ThatStillBracketTheData()
    {
        string[] values = Enumerable.Range(0, 50).Select(i => new string((char)('b' + i % 20), 300) + i).Append(new string('a', 500)).Append(new string('x', 400)).ToArray();
        var node = new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);

        var statistics = TestParquetFile.Parse(WriteOne(node, w => w.WriteStrings(values))).Chunk(0, 0).Statistics!;

        await Assert.That(statistics.MinValue!.Length).IsEqualTo(64);
        await Assert.That(statistics.MaxValue!.Length).IsLessThanOrEqualTo(64);
        await Assert.That(statistics.IsMinValueExact).IsFalse();
        await Assert.That(statistics.IsMaxValueExact).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(statistics.MinValue)).IsEqualTo(new string('a', 64));
        await Assert.That(Encoding.UTF8.GetString(statistics.MaxValue)).IsEqualTo(new string('x', 63) + "y"); // the largest value, shortened and grown
    }

    [Test]
    public async Task TheTruncationLength_CanBeChanged()
    {
        string[] values = [new string('m', 100), new string('n', 100)];
        static PrimitiveNode Node() => new("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);

        var whole = TestParquetFile.Parse(WriteOne(Node(), w => w.WriteStrings(values), new ParquetWriterOptions { StatisticsTruncateLength = 1000 })).Chunk(0, 0).Statistics!;
        var short8 = TestParquetFile.Parse(WriteOne(Node(), w => w.WriteStrings(values), new ParquetWriterOptions { StatisticsTruncateLength = 8 })).Chunk(0, 0).Statistics!;

        await Assert.That(whole.MinValue!.Length).IsEqualTo(100);
        await Assert.That(whole.IsMinValueExact).IsTrue();
        await Assert.That(short8.MinValue!.Length).IsEqualTo(8);
        await Assert.That(short8.IsMinValueExact).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(short8.MaxValue!)).IsEqualTo(new string('n', 7) + "o");
    }

    [Test]
    public async Task ABinaryMaximumThatCannotBeShortened_IsLeftOut()
    {
        var bytes = WriteOne(new PrimitiveNode("b", PhysicalType.ByteArray, FieldRepetitionType.Required), w => w.WriteByteArrays([Enumerable.Repeat((byte)0xFF, 200).ToArray(), new byte[] { 1 }]), new ParquetWriterOptions { StatisticsTruncateLength = 16 });
        var raw = TestParquetFile.Parse(bytes).Chunk(0, 0).Statistics!;

        await Assert.That(raw.MaxValue).IsNull();
        await Assert.That(raw.MinValue).IsEquivalentTo(new byte[] { 1 });
    }
}
