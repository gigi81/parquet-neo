using ParquetNeo.Exceptions;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Filtering;

/// <summary>Bloom filters written to files, read back, and used to rule out row groups the statistics cannot.</summary>
public class BloomFilterFileTests
{
    private static readonly BloomFilterSettings Exact = new() { FalsePositiveRate = 0.000001 };

    /// <summary>Three row groups whose values are spread over the same range: 0, 3, 6... then 1, 4, 7... then 2, 5, 8..., so that the minimum and the maximum rule nothing out.</summary>
    private static T[][] Interleaved<T>(Func<int, T> make, int perGroup = 40) =>
        Enumerable.Range(0, 3).Select(group => Enumerable.Range(0, perGroup).Select(i => make((i * 3) + group)).ToArray()).ToArray();

    private static ParquetFileReader Write<T>(string column, T[][] groups, Action<ColumnWriter, T[]> write, bool bloom = true) =>
        FilterFile.Create(
            [FilterFile.Column<T>(column)],
            groups.Length,
            (index, writer) => write(writer.NextColumn(), groups[index]),
            bloom ? new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { [column] = Exact } } : null);

    /// <summary>Every value of every group finds its group (no false negatives), and values that are inside the range of the column but in none find almost none; the statistics are no help, as without the filters shows.</summary>
    private static async Task CheckLookups<T>(string column, T[][] groups, Action<ColumnWriter, T[]> write, Func<int, T> absent)
    {
        using var file = Write(column, groups, write);

        for (int group = 0; group < groups.Length; group++)
        {
            foreach (var value in groups[group].Take(8))
            {
                var kept = file.GetMatchingRowGroups(ParquetFilter.Column(column).Equal(value!));
                await Assert.That(kept).Contains(group);
            }
        }

        int skippedEverywhere = Enumerable.Range(0, 30).Count(i => file.GetMatchingRowGroups(ParquetFilter.Column(column).Equal(absent(i)!)).Count == 0);
        await Assert.That(skippedEverywhere).IsGreaterThanOrEqualTo(28);

        // Nothing is ruled out by the statistics here, so it is the filters that did it.
        using var without = Write(column, groups, write, bloom: false);
        await Assert.That(without.GetMatchingRowGroups(ParquetFilter.Column(column).Equal(absent(10)!)).Count).IsEqualTo(3);
    }

    [Test]
    public async Task Integers() => await CheckLookups("v", Interleaved(i => i * 7), (w, v) => w.WriteBatch(v), i => (i * 7) + 1);

    [Test]
    public async Task Longs() => await CheckLookups("v", Interleaved(i => (long)i * 1_000_003 - 50), (w, v) => w.WriteBatch(v), i => (i * 1_000_003L) - 49);

    [Test]
    public async Task UnsignedIntegers() =>
        await CheckLookups("v", Interleaved(i => 3_000_000_000u + (uint)(i * 5)), (w, v) => w.WriteBatch(v), i => 3_000_000_001u + (uint)(i * 5));

    [Test]
    public async Task UnsignedLongs() =>
        await CheckLookups("v", Interleaved(i => ulong.MaxValue - (ulong)(i * 11)), (w, v) => w.WriteBatch(v), i => ulong.MaxValue - (ulong)(i * 11) - 1);

    [Test]
    public async Task SmallIntegers()
    {
        await CheckLookups("v", Interleaved(i => (short)((i * 13) - 300)), (w, v) => w.WriteBatch(v), i => (short)((i * 13) - 299));
        await CheckLookups("v", Interleaved(i => (byte)((i % 60) * 2)), (w, v) => w.WriteBatch(v), i => (byte)(((i % 60) * 2) + 1));
    }

    [Test]
    public async Task Doubles() => await CheckLookups("v", Interleaved(i => (i * 0.37) + 1.5), (w, v) => w.WriteBatch(v), i => (i * 0.37) + 1.51);

    [Test]
    public async Task Floats() => await CheckLookups("v", Interleaved(i => (i * 0.5f) + 1.25f), (w, v) => w.WriteBatch(v), i => (i * 0.5f) + 1.125f);

    [Test]
    public async Task Text() => await CheckLookups("v", Interleaved(i => $"value-{i * 17:D6}"), (w, v) => w.WriteStrings(v), i => $"value-{i * 17:D6}!");

    [Test]
    public async Task Guids() => await CheckLookups("v", Interleaved(i => new Guid(i, 5, 6, [1, 2, 3, 4, 5, 6, 7, 8])), (w, v) => w.WriteBatch(v), i => new Guid(i, 9, 9, [9, 9, 9, 9, 9, 9, 9, 9]));

    [Test]
    public async Task Dates() => await CheckLookups("v", Interleaved(i => new DateOnly(2000, 1, 1).AddDays(i * 3)), (w, v) => w.WriteBatch(v), i => new DateOnly(2000, 1, 1).AddDays((i * 3) + 1));

    [Test]
    public async Task TimesOfDay() =>
        await CheckLookups("v", Interleaved(i => new TimeOnly(1, 0).Add(TimeSpan.FromSeconds(i * 37))), (w, v) => w.WriteBatch(v), i => new TimeOnly(1, 0).Add(TimeSpan.FromSeconds((i * 37) + 1)));

    [Test]
    public async Task Timestamps()
    {
        await CheckLookups("v", Interleaved(i => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 13)), (w, v) => w.WriteBatch(v), i => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes((i * 13) + 1));
        await CheckLookups("v", Interleaved(i => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i * 13)), (w, v) => w.WriteBatch(v), i => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes((i * 13) + 1));
    }

    [Test]
    public async Task ByteArraysOfAnyKind()
    {
        // Binary values go in as byte arrays, with their own length; a lookup finds the same bytes.
        var groups = Interleaved(i => new byte[] { (byte)i, (byte)(i >> 8), 7 });
        using var file = FilterFile.Create(
            [FilterFile.Column<byte[]>("v")],
            3,
            (index, writer) => writer.NextColumn().WriteByteArrays(groups[index].Select(b => (ReadOnlyMemory<byte>)b).ToArray()),
            new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["v"] = Exact } });

        for (int group = 0; group < 3; group++)
        {
            foreach (var value in groups[group].Take(8))
            {
                await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(value))).Contains(group);
            }
        }

        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(new byte[] { 200, 200, 7 })).Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheValuesTheStatisticsCannotRuleOut_ButTheFilterCan_AreRuledOutInTheMiddleOfTheRange()
    {
        using var file = Write("v", Interleaved(i => i), (w, v) => w.WriteBatch(v));

        // 100 is in the range of every row group, and in group 1.
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(100)).ToArray()).IsEquivalentTo(new[] { 1 });
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").In(0, 101, 5000)).ToArray()).IsEquivalentTo(new[] { 0, 2 });
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(100) & ParquetFilter.Column("v").Equal(101)).Count).IsEqualTo(0);
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").NotEqual(100)).Count).IsEqualTo(3);
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").GreaterThan(100)).Count).IsEqualTo(3);

        // A value that no integer column of the width holds is certainly not there.
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(long.MaxValue)).Count).IsEqualTo(0);
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(-5)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTheColumnsThatAskedForFilters_HaveThem()
    {
        var columns = new[] { FilterFile.Column<int>("a"), FilterFile.Column<int>("b") };
        using var file = FilterFile.Create(
            columns,
            1,
            (_, writer) =>
            {
                writer.NextColumn().WriteBatch(new[] { 1, 2, 3 });
                writer.NextColumn().WriteBatch(new[] { 4, 5, 6 });
            },
            new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["b"] = new() } });

        var chunks = file.Metadata.RowGroups[0].Columns;

        await Assert.That(chunks[0].MetaData!.BloomFilterOffset).IsNull();
        await Assert.That(chunks[1].MetaData!.BloomFilterOffset).IsNotNull();
        await Assert.That(chunks[1].MetaData!.BloomFilterLength!.Value).IsGreaterThan(32);
    }

    [Test]
    public async Task Dictionaries_AndPlainPages_AndAFallbackFromOneToTheOther_AreAllCovered()
    {
        var all = Enumerable.Range(0, 3000).Select(i => $"key-{i * 7919 % 100_003}").ToArray();
        foreach (var options in new[]
        {
            new ParquetWriterOptions { DictionaryEnabled = true },
            new ParquetWriterOptions { DictionaryEnabled = false },
            new ParquetWriterOptions { DictionaryEnabled = true, DictionaryPageSizeLimit = 2000, DataPageSize = 1000 },
            new ParquetWriterOptions { DictionaryEnabled = false, DataPageVersion = DataPageVersion.V2, Compression = CompressionCodec.Gzip, DataPageSize = 1000 },
        })
        {
            var configured = options with { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["v"] = Exact } };
            using var file = FilterFile.Create([FilterFile.Column<string>("v")], 1, (_, writer) => writer.NextColumn().WriteStrings(all), configured);

            await Assert.That(all.All(value => file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(value)).Count == 1)).IsTrue();
            await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal("key-absent")).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ASizeLimit_MakesASmallerFilterThatStillHasNoFalseNegatives()
    {
        var values = Enumerable.Range(0, 20_000).Select(i => (long)i * 1_000_003).ToArray();
        using var file = FilterFile.Create(
            [FilterFile.Column<long>("v")],
            1,
            (_, writer) => writer.NextColumn().WriteBatch(values),
            new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["v"] = new() { FalsePositiveRate = 0.01, MaxBytes = 1024 } } });

        await Assert.That(file.Metadata.RowGroups[0].Columns[0].MetaData!.BloomFilterLength!.Value).IsLessThan(1100);
        await Assert.That(values.Take(500).All(value => file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(value)).Count == 1)).IsTrue();
    }

    [Test]
    public async Task AFilterThatCannotBeRead_IsLeftAlone()
    {
        byte[] bytes = FilterFile.CreateBytes(
            [FilterFile.Column<int>("v")],
            2,
            (group, writer) => writer.NextColumn().WriteBatch(Enumerable.Range(group * 100, 100).Select(i => i * 2).ToArray()),
            new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["v"] = Exact } });
        long offset;
        using (var reader = new ParquetFileReader(new MemoryStream(bytes)))
        {
            offset = reader.Metadata.RowGroups[0].Columns[0].MetaData!.BloomFilterOffset!.Value;
        }

        // The first byte of the header of the filter of the first row group: damaged.
        var damaged = (byte[])bytes.Clone();
        damaged[offset] = 0xFF;
        damaged[offset + 1] = 0xFF;
        using var broken = new ParquetFileReader(new MemoryStream(damaged));

        // 19 is not in the first group; the statistics cannot say (the range is 0 to 198), and the filter that could is unreadable.
        await Assert.That(broken.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(19)).ToArray()).IsEquivalentTo(new[] { 0 });
        await Assert.That(broken.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(1))).Contains(0);
    }

    [Test]
    public async Task TheSettings_AreChecked_WhenTheWriterIsCreated()
    {
        var schema = new ParquetSchema([FilterFile.Column<int>("a"), FilterFile.Column<bool>("flag")]);

        Action<IReadOnlyDictionary<string, BloomFilterSettings>> create = settings =>
            new ParquetFileWriter(new MemoryStream(), schema, new ParquetWriterOptions { ColumnBloomFilters = settings }).Dispose();

        await Assert.That(() => create(new Dictionary<string, BloomFilterSettings> { ["missing"] = new() })).Throws<ArgumentException>();
        await Assert.That(() => create(new Dictionary<string, BloomFilterSettings> { ["flag"] = new() })).Throws<ArgumentException>();
        await Assert.That(() => create(new Dictionary<string, BloomFilterSettings> { ["a"] = new() { FalsePositiveRate = 0 } })).Throws<ArgumentException>();
        await Assert.That(() => create(new Dictionary<string, BloomFilterSettings> { ["a"] = new() { FalsePositiveRate = 1 } })).Throws<ArgumentException>();
        await Assert.That(() => create(new Dictionary<string, BloomFilterSettings> { ["a"] = new() { MaxBytes = 8 } })).Throws<ArgumentException>();
    }

    [Test]
    public async Task ZeroAndNegativeZero_AreOneNumber_SoNeitherRulesOutTheOther()
    {
        using var file = Write("v", new[] { new[] { -0.0, 1.5 }, new[] { 2.5, 3.5 } }, (w, v) => w.WriteBatch(v));

        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(0.0)).ToArray()).IsEquivalentTo(new[] { 0 });
    }

    [Test]
    public async Task ADoubleThatAFloatCannotBe_IsNotInAFloatColumn()
    {
        using var file = Write("v", new[] { new[] { 0.5f, 1.25f }, new[] { 100f, 200f } }, (w, v) => w.WriteBatch(v));

        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(0.1)).Count).IsEqualTo(0);
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(1.25)).ToArray()).IsEquivalentTo(new[] { 0 });
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(100)).ToArray()).IsEquivalentTo(new[] { 1 });
    }

    [Test]
    public async Task ATimestampOfAFinerUnitThanTheColumn_IsNotInTheColumn()
    {
        var start = new DateTime(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var groups = new[] { new[] { start, start.AddSeconds(1) }, new[] { start.AddSeconds(2), start.AddSeconds(3) } };
        using var file = Write("v", groups, (w, v) => w.WriteBatch(v));

        // Microseconds are what a DateTime column holds: 5 ticks are half of one.
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(start.AddSeconds(1).AddTicks(5))).Count).IsEqualTo(0);
        await Assert.That(file.GetMatchingRowGroups(ParquetFilter.Column("v").Equal(start.AddSeconds(1))).ToArray()).IsEquivalentTo(new[] { 0 });
    }
}
