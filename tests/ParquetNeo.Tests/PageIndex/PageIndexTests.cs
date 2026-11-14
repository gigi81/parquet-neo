using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Filtering;

namespace ParquetNeo.Tests.PageIndex;

/// <summary>The page index written to files, read back, used to skip pages, and the columns read for only some rows.</summary>
public class PageIndexTests
{
    private const int Rows = 5000;

    private static ParquetWriterOptions Options(bool pageIndex = true, DataPageVersion version = DataPageVersion.V1, CompressionCodec codec = CompressionCodec.Uncompressed) =>
        new() { WritePageIndex = pageIndex, DataPageSize = 400, DataPageVersion = version, Compression = codec };

    private static ParquetFileReader SortedLongs(ParquetWriterOptions options, int rowGroups = 1) =>
        FilterFile.Create(
            [FilterFile.Column<long>("id"), FilterFile.Column<string>("name")],
            rowGroups,
            (group, writer) =>
            {
                var ids = Enumerable.Range(0, Rows).Select(i => (long)(group * Rows) + i).ToArray();
                writer.NextColumn().WriteBatch(ids);
                writer.NextColumn().WriteStrings(ids.Select(i => "n" + i.ToString("D8")).ToArray());
            },
            options);

    private static long[] ReadLongs(RowGroupReader group, int column, RowSelection? selection)
    {
        using var reader = selection is null ? group.OpenColumn(column) : group.OpenColumn(column, selection);
        var all = new List<long>();
        var batch = new long[777];
        BatchRead read;
        while ((read = reader.ReadBatch(batch)).Entries > 0)
        {
            all.AddRange(batch.AsSpan(0, read.Entries).ToArray());
        }

        return all.ToArray();
    }

    private static string[] ReadStrings(RowGroupReader group, int column, RowSelection? selection)
    {
        using var reader = selection is null ? group.OpenColumn(column) : group.OpenColumn(column, selection);
        var all = new List<string>();
        var batch = new ReadOnlyMemory<byte>[333];
        BatchRead read;
        while ((read = reader.ReadByteArrays(batch)).Entries > 0)
        {
            all.AddRange(batch.Take(read.Entries).Select(b => System.Text.Encoding.UTF8.GetString(b.Span)));
        }

        return all.ToArray();
    }

    [Test]
    public async Task TheIndexIsRecordedForEveryColumnAndMatchesThePages()
    {
        using var file = SortedLongs(Options());
        var group = file.Metadata.RowGroups[0];
        foreach (var chunk in group.Columns)
        {
            await Assert.That(chunk.OffsetIndexOffset).IsNotNull();
            await Assert.That(chunk.ColumnIndexOffset).IsNotNull();
            await Assert.That(chunk.OffsetIndexOffset!.Value).IsGreaterThan(chunk.MetaData!.DataPageOffset);
        }

        var pages = file.ReadColumnPages(group.Columns[0], Rows)!;
        await Assert.That(pages.Locations.Count).IsGreaterThan(10);
        await Assert.That(pages.Locations[0].FirstRowIndex).IsEqualTo(0);

        // The bounds of every page are those of the values in it: ids ascend, so a page holds the ids from its first row to the next page's.
        for (int page = 0; page < pages.Locations.Count; page++)
        {
            var (first, end) = pages.RowsOf(page, Rows);
            await Assert.That(BitConverter.ToInt64(pages.Bounds!.MinValues[page])).IsEqualTo(first);
            await Assert.That(BitConverter.ToInt64(pages.Bounds.MaxValues[page])).IsEqualTo(end - 1);
            await Assert.That(pages.Bounds.NullCounts![page]).IsEqualTo(0);
        }

        // The pages follow one another, from the first data page to the end of the chunk (the dictionary page, if any, is in front).
        for (int page = 1; page < pages.Locations.Count; page++)
        {
            await Assert.That(pages.Locations[page].Offset).IsEqualTo(pages.Locations[page - 1].Offset + pages.Locations[page - 1].CompressedPageSize);
        }

        var meta = group.Columns[0].MetaData!;
        await Assert.That(pages.Locations[0].Offset).IsEqualTo(meta.DataPageOffset);
        var last = pages.Locations[^1];
        await Assert.That(last.Offset + last.CompressedPageSize).IsEqualTo((meta.DictionaryPageOffset is > 0 ? meta.DictionaryPageOffset.Value : meta.DataPageOffset) + meta.TotalCompressedSize);
    }

    [Test]
    public async Task WithoutTheOptionThereIsNoIndex()
    {
        using var file = SortedLongs(Options(pageIndex: false));
        await Assert.That(file.Metadata.RowGroups[0].Columns[0].OffsetIndexOffset).IsNull();
        var group = file.OpenRowGroup(0);
        await Assert.That(group.SelectRows(ParquetFilter.Column("id").Equal(5L), [0])).IsNull();
        await Assert.That(() => group.OpenColumn(0, new RowSelection([new RowRange(0, 1)]))).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(DataPageVersion.V1, CompressionCodec.Uncompressed)]
    [Arguments(DataPageVersion.V2, CompressionCodec.Uncompressed)]
    [Arguments(DataPageVersion.V1, CompressionCodec.Gzip)]
    [Arguments(DataPageVersion.V2, CompressionCodec.Gzip)]
    public async Task ASelectionReadsExactlyItsRows(DataPageVersion version, CompressionCodec codec)
    {
        using var file = SortedLongs(Options(version: version, codec: codec));
        var group = file.OpenRowGroup(0);
        var random = new Random(7);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var ranges = new List<RowRange>();
            long next = random.Next(0, 400);
            while (next < Rows && ranges.Count < 8)
            {
                long count = random.Next(1, 700);
                count = Math.Min(count, Rows - next);
                ranges.Add(new RowRange(next, count));
                next += count + random.Next(1, 900);
            }

            var selection = new RowSelection(ranges);
            var expectedIds = ranges.SelectMany(r => Enumerable.Range((int)r.Start, (int)r.Count).Select(i => (long)i)).ToArray();
            await Assert.That(ReadLongs(group, 0, selection)).IsEquivalentTo(expectedIds);
            await Assert.That(ReadStrings(group, 1, selection)).IsEquivalentTo(expectedIds.Select(i => "n" + i.ToString("D8")).ToArray());
        }
    }

    [Test]
    public async Task AnEmptySelectionReadsNothing()
    {
        using var file = SortedLongs(Options());
        var group = file.OpenRowGroup(0);
        await Assert.That(ReadLongs(group, 0, new RowSelection([]))).IsEmpty();
    }

    [Test]
    public async Task AFilterSelectsOnlyThePagesThatMayMatch()
    {
        using var file = SortedLongs(Options());
        var group = file.OpenRowGroup(0);
        var filter = ParquetFilter.Column("id").GreaterThanOrEqual(2000L) & ParquetFilter.Column("id").LessThan(2100L);
        var selection = group.SelectRows(filter, [0, 1])!;

        await Assert.That(selection).IsNotNull();
        await Assert.That(selection.RowCount).IsLessThan(Rows / 5);
        var rows = ReadLongs(group, 0, selection);
        await Assert.That(rows.Where(i => i is >= 2000 and < 2100).Count()).IsEqualTo(100);
        await Assert.That(ReadStrings(group, 1, selection).Length).IsEqualTo(rows.Length);
    }

    [Test]
    public async Task AFilterThatRulesOutEverythingSelectsNothing()
    {
        using var file = SortedLongs(Options());
        var group = file.OpenRowGroup(0);
        var selection = group.SelectRows(ParquetFilter.Column("id").Equal(2500L) & ParquetFilter.Column("id").GreaterThan(4000L), [0]);
        await Assert.That(selection).IsNotNull();
        await Assert.That(selection!.RowCount).IsEqualTo(0);
    }

    [Test]
    public async Task AFilterThatKeepsEveryPageSelectsNull()
    {
        using var file = SortedLongs(Options());
        await Assert.That(file.OpenRowGroup(0).SelectRows(ParquetFilter.Column("id").GreaterThanOrEqual(0L), [0])).IsNull();
    }

    [Test]
    public async Task NotAndOrArePageAware()
    {
        using var file = SortedLongs(Options());
        var group = file.OpenRowGroup(0);
        var either = ParquetFilter.Column("id").LessThan(100L) | ParquetFilter.Column("id").GreaterThanOrEqual(4900L);
        var selection = group.SelectRows(either, [0])!;
        await Assert.That(selection.RowCount).IsLessThan(Rows / 5);
        var rows = ReadLongs(group, 0, selection);
        await Assert.That(rows.Count(i => i < 100 || i >= 4900)).IsEqualTo(200);

        var notInside = !(ParquetFilter.Column("id").GreaterThanOrEqual(100L) & ParquetFilter.Column("id").LessThan(4900L));
        var other = group.SelectRows(notInside, [0])!;
        await Assert.That(ReadLongs(group, 0, other).Count(i => i < 100 || i >= 4900)).IsEqualTo(200);
    }

    [Test]
    public async Task EveryRowThatSatisfiesARandomFilterIsSelected()
    {
        // Values with nulls, in an order that makes the bounds of the pages tight but not useless.
        var random = new Random(11);
        var values = Enumerable.Range(0, Rows).Select(i => (i / 40) * 10 + random.Next(0, 25)).ToArray();
        var nulls = Enumerable.Range(0, Rows).Select(i => random.Next(0, 9) == 0).ToArray();
        using var file = FilterFile.Create(
            [FilterFile.Column<int>("v", optional: true)],
            1,
            (_, writer) =>
            {
                var present = values.Where((_, i) => !nulls[i]).ToArray();
                writer.NextColumn().WriteBatch(present, nulls.Select(n => n ? 0 : 1).ToArray());
            },
            Options());
        var group = file.OpenRowGroup(0);

        for (int attempt = 0; attempt < 80; attempt++)
        {
            int low = random.Next(-5, 1300);
            int high = low + random.Next(0, 200);
            var column = ParquetFilter.Column("v");
            (ParquetFilter Filter, Func<int?, bool> Row) condition = random.Next(0, 5) switch
            {
                0 => (column.GreaterThanOrEqual(low) & column.LessThan(high), v => v >= low && v < high),
                1 => (column.Equal(low), v => v == low),
                2 => (column.LessThan(low) | column.IsNull(), v => v is null || v < low),
                3 => (!column.GreaterThan(high), v => v is not null && !(v > high)),
                _ => (column.In(low, high, low + 7), v => v == low || v == high || v == low + 7),
            };

            var selection = group.SelectRows(condition.Filter, [0]);
            var selected = selection is null
                ? Enumerable.Range(0, Rows).ToHashSet()
                : selection.Ranges.SelectMany(r => Enumerable.Range((int)r.Start, (int)r.Count)).ToHashSet();
            for (int row = 0; row < Rows; row++)
            {
                int? value = nulls[row] ? null : values[row];
                if (condition.Row(value) && !selected.Contains(row))
                {
                    throw new InvalidOperationException($"Row {row} (value {value}) satisfies the condition of attempt {attempt} but is not selected.");
                }
            }
        }

        await Task.CompletedTask;
    }
}
