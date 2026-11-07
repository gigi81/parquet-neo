using System.Text;
using ParquetNeo.Core;
using ParquetNeo.Core.Statistics;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = ParquetNeo.Metadata.Statistics;

public class StatisticsReaderTests
{
    // ---- statistics written by pyarrow for the sample dataset ----

    [Test]
    [Arguments("pyarrow-flat-optional-statistics.parquet", true)]
    [Arguments("pyarrow-flat-required-statistics-dictionary.parquet", false)]
    public async Task ThePyArrowSampleStatistics_MatchTheData(string fixture, bool optional)
    {
        using var reader = ReadHelper.OpenFixture(fixture);

        await Assert.That(reader.RowGroupCount).IsEqualTo(3);
        for (int group = 0; group < 3; group++)
        {
            int from = group * 400;
            int count = group == 2 ? 200 : 400;
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                var statistics = reader.OpenRowGroup(group).GetStatistics(column)!;
                var (min, max, nulls) = StatisticsWriterTests.Expected(optional, from, count, column);

                string where = $"{fixture} group {group} {SampleFile.ColumnNames[column]}";
                await Assert.That(SampleFile.Canonical(statistics.Min)).IsEqualTo(SampleFile.Canonical(min)).Because(where);
                await Assert.That(SampleFile.Canonical(statistics.Max)).IsEqualTo(SampleFile.Canonical(max)).Because(where);
                await Assert.That(statistics.NullCount).IsEqualTo(nulls).Because(where);
            }
        }
    }

    [Test]
    public async Task FilesWithoutStatistics_ReturnNull()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-flat-optional-v1.parquet"); // written with write_statistics=False

        await Assert.That(reader.OpenRowGroup(0).GetStatistics(0)).IsNull();
        await Assert.That(reader.OpenRowGroup(0).GetStatistics("i64")).IsNull();
    }

    // ---- awkward values, from pyarrow ----

    [Test]
    [Arguments("pyarrow-statistics-plain.parquet")]
    [Arguments("pyarrow-statistics-dictionary.parquet")]
    public async Task TheAwkwardCases_ReadAsPyArrowWroteThem(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);
        var group = reader.OpenRowGroup(0);
        static long Bits(object? value) => BitConverter.DoubleToInt64Bits((double)value!);

        // NaNs are not bounds; the null is not either.
        await Assert.That(group.GetStatistics("f64")!.Min).IsEqualTo(-7.25);
        await Assert.That(group.GetStatistics("f64")!.Max).IsEqualTo(2.5);
        await Assert.That(group.GetStatistics("f64")!.NullCount).IsEqualTo(1L);
        await Assert.That(group.GetStatistics("f32")!.Min).IsEqualTo(-2.0f);
        await Assert.That(group.GetStatistics("f32")!.Max).IsEqualTo(9.0f);

        // Every value NaN: no bounds.
        await Assert.That(group.GetStatistics("all_nan")!.Min).IsNull();
        await Assert.That(group.GetStatistics("all_nan")!.Max).IsNull();

        // Zeros of either sign: -0.0 as the minimum, +0.0 as the maximum.
        await Assert.That(Bits(group.GetStatistics("zeros")!.Min)).IsEqualTo(BitConverter.DoubleToInt64Bits(-0.0));
        await Assert.That(Bits(group.GetStatistics("zeros")!.Max)).IsEqualTo(BitConverter.DoubleToInt64Bits(0.0));

        // Unsigned columns are stored as the signed type and ordered unsigned.
        await Assert.That(group.GetStatistics("u32")!.Min).IsEqualTo(0);
        await Assert.That(group.GetStatistics("u32")!.Max).IsEqualTo(unchecked((int)4_000_000_000));
        await Assert.That(group.GetStatistics("u64")!.Min).IsEqualTo(0L);
        await Assert.That(group.GetStatistics("u64")!.Max).IsEqualTo(-1L);
        await Assert.That(group.GetStatistics("i64")!.Min).IsEqualTo(long.MinValue);
        await Assert.That(group.GetStatistics("i64")!.Max).IsEqualTo(long.MaxValue);

        // Binary data orders as unsigned bytes: "é" is after "z".
        await Assert.That(Encoding.UTF8.GetString((byte[])group.GetStatistics("text")!.Min!)).IsEqualTo("");
        await Assert.That(Encoding.UTF8.GetString((byte[])group.GetStatistics("text")!.Max!)).IsEqualTo("é");
        await Assert.That((byte[])group.GetStatistics("blob")!.Min!).IsEquivalentTo(Array.Empty<byte>());
        await Assert.That((byte[])group.GetStatistics("blob")!.Max!).IsEquivalentTo(new byte[] { 0xFF });
        await Assert.That((byte[])group.GetStatistics("fixed")!.Min!).IsEquivalentTo(new byte[] { 0x00, 0x00 });
        await Assert.That((byte[])group.GetStatistics("fixed")!.Max!).IsEquivalentTo(new byte[] { 0xFF, 0xFF });

        // Decimals order by value: -300.00 is -30000 and 99999.99 is 9999999, as 5-byte big-endian numbers.
        await Assert.That((byte[])group.GetStatistics("money")!.Min!).IsEquivalentTo(new byte[] { 0xFF, 0xFF, 0xFF, 0x8A, 0xD0 });
        await Assert.That((byte[])group.GetStatistics("money")!.Max!).IsEquivalentTo(new byte[] { 0x00, 0x00, 0x98, 0x96, 0x7F });

        await Assert.That(group.GetStatistics("flag")!.Min is true).IsTrue();
        await Assert.That(group.GetStatistics("flag_mixed")!.Min is false).IsTrue();
        await Assert.That(group.GetStatistics("flag_mixed")!.Max is true).IsTrue();

        await Assert.That(group.GetStatistics("all_null")!.Min).IsNull();
        await Assert.That(group.GetStatistics("all_null")!.NullCount).IsEqualTo(7L);
    }

    [Test]
    public async Task NestedColumns_HaveStatistics_EvenThoughTheyCannotBeRead()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-v1.parquet");
        var group = reader.OpenRowGroup(0);
        int element = reader.Schema.FindColumn(["tags", "list", "element"])!.ColumnIndex;

        var statistics = group.GetStatistics(element)!;

        // The values are what the file records, which is all this checks: they are the same ones pyarrow itself reports.
        await Assert.That(Encoding.UTF8.GetString((byte[])statistics.Min!)).IsEqualTo("a");
        await Assert.That(statistics.Max).IsNotNull();
        await Assert.That(statistics.NullCount).IsNotNull();
    }

    // ---- statistics that cannot be trusted ----

    private static byte[] StatisticsFile() => File.ReadAllBytes(TestBytes.FixturePath("pyarrow-statistics-plain.parquet"));

    private static ColumnStatistics? Read(byte[] bytes, string column)
    {
        using var reader = ReadHelper.Open(bytes);
        return reader.OpenRowGroup(0).GetStatistics(column);
    }

    private static void Edit(ref byte[] bytes, string column, Action<Statistics> change)
    {
        int index = TestParquetFile.Parse(bytes).Schema.FindColumn([column])!.ColumnIndex;
        bytes = FooterEditor.Rewrite(bytes, f => change(f.RowGroups[0].Columns[index].MetaData!.Statistics!));
    }

    [Test]
    public async Task WhenTheColumnOrderIsUnknown_TheBoundsAreIgnored_ButTheCountsAreKept()
    {
        foreach (var orders in new List<ColumnOrder>?[] { null, Enumerable.Repeat(ColumnOrder.Unrecognized, 14).ToList(), Enumerable.Repeat(ColumnOrder.Int96Timestamp, 14).ToList() })
        {
            var bytes = FooterEditor.Rewrite(StatisticsFile(), f => f.ColumnOrders = orders);

            var statistics = Read(bytes, "i64")!;

            await Assert.That(statistics.Min).IsNull();
            await Assert.That(statistics.Max).IsNull();
            await Assert.That(statistics.NullCount).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task TheDeprecatedMinAndMaxFields_AreNotUsed()
    {
        var bytes = StatisticsFile();
        Edit(ref bytes, "i64", s =>
        {
            s.Min = BitConverter.GetBytes(3L);
            s.Max = BitConverter.GetBytes(4L);
            s.MinValue = null;
            s.MaxValue = null;
        });

        var statistics = Read(bytes, "i64")!;

        await Assert.That(statistics.Min).IsNull();
        await Assert.That(statistics.Max).IsNull();
    }

    [Test]
    public async Task ANaNBound_IsDropped()
    {
        var bytes = StatisticsFile();
        Edit(ref bytes, "f64", s =>
        {
            s.MinValue = BitConverter.GetBytes(double.NaN);
            s.MaxValue = BitConverter.GetBytes(12.0);
        });

        var statistics = Read(bytes, "f64")!;

        await Assert.That(statistics.Min).IsNull();
        await Assert.That(statistics.Max).IsEqualTo(12.0);
        await Assert.That(statistics.IsMinExact).IsNull();
    }

    [Test]
    public async Task BoundsOfTheWrongSize_AreReported()
    {
        foreach (var (column, size) in new[] { ("i64", 4), ("i64", 9), ("f32", 8), ("flag", 0), ("fixed", 3), ("u32", 8) })
        {
            var bytes = StatisticsFile();
            Edit(ref bytes, column, s => s.MinValue = new byte[size]);

            await Assert.That(() => Read(bytes, column)).Throws<MetadataException>().Because($"{column} with {size} bytes");
        }
    }

    [Test]
    public async Task TheExactFlags_ArePassedThrough()
    {
        var bytes = StatisticsFile();
        Edit(ref bytes, "text", s =>
        {
            s.IsMinValueExact = false;
            s.IsMaxValueExact = true;
        });

        var statistics = Read(bytes, "text")!;

        await Assert.That(statistics.IsMinExact).IsFalse();
        await Assert.That(statistics.IsMaxExact).IsTrue();
    }

    [Test]
    public async Task AskingForTheStatisticsOfAnUnknownColumn_IsAnError()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-statistics-plain.parquet");
        var group = reader.OpenRowGroup(0);

        await Assert.That(() => group.GetStatistics("nope")).Throws<ArgumentException>();
        await Assert.That(() => group.GetStatistics(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => group.GetStatistics(14)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => group.GetStatistics(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task StatisticsOfAClosedFile_CannotBeRead()
    {
        var reader = ReadHelper.OpenFixture("pyarrow-statistics-plain.parquet");
        var group = reader.OpenRowGroup(0);
        reader.Dispose();

        await Assert.That(() => group.GetStatistics(0)).Throws<ObjectDisposedException>();
    }
}
