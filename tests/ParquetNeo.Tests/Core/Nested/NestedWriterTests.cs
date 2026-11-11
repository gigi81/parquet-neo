using ParquetNeo.Core;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

public class NestedWriterTests
{
    private static readonly StructValue[] AllRows = NestedDataset.Rows().ToArray();

    /// <summary>Checks a file against the shredded dataset using the test mini reader and the library's reader.</summary>
    private static async Task AssertFileHolds(byte[] bytes, IReadOnlyList<StructValue> rows, int rowsPerGroup = int.MaxValue)
    {
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        using var reader = ReadHelper.Open(bytes);

        int groups = (int)(((long)rows.Count + rowsPerGroup - 1) / rowsPerGroup);
        await Assert.That(file.Metadata.RowGroups.Count).IsEqualTo(groups);
        for (int group = 0; group < groups; group++)
        {
            var expected = ReferenceShredder.Shred(file.Schema, rows.Skip((int)Math.Min((long)group * rowsPerGroup, rows.Count)).Take(rowsPerGroup));
            await Assert.That(file.Metadata.RowGroups[group].NumRows).IsEqualTo((long)expected[0].RowCount);
            for (int column = 0; column < expected.Length; column++)
            {
                string where = $"group {group} {string.Join('.', expected[column].Column.Path)}";
                var mini = file.ReadEntries(group, column);
                await Assert.That(mini.Definition).IsEquivalentTo(expected[column].Definition).Because("mini reader, " + where);
                await Assert.That(mini.Repetition).IsEquivalentTo(expected[column].Repetition).Because("mini reader, " + where);
                await Assert.That(mini.Values.Select(NestedReaderTests.Describe).SequenceEqual(expected[column].Values.Select(NestedReaderTests.Describe))).IsTrue().Because("mini reader, " + where);

                foreach (int batch in new[] { 1, 50 })
                {
                    var (definition, repetition, values) = ReadHelper.ReadEntries(reader, group, column, batch);
                    await Assert.That(definition).IsEquivalentTo(expected[column].Definition).Because($"reader batch {batch}, {where}");
                    await Assert.That(repetition).IsEquivalentTo(expected[column].Repetition).Because($"reader batch {batch}, {where}");
                    await Assert.That(values.Select(NestedReaderTests.Describe).SequenceEqual(expected[column].Values.Select(NestedReaderTests.Describe))).IsTrue().Because($"reader batch {batch}, {where}");
                }
            }
        }
    }

    [Test]
    [Arguments(true, CompressionCodec.Uncompressed, 1 << 20, int.MaxValue)]
    [Arguments(false, CompressionCodec.Uncompressed, 1 << 20, int.MaxValue)]
    [Arguments(false, CompressionCodec.Uncompressed, 200, int.MaxValue)]
    [Arguments(true, CompressionCodec.Uncompressed, 200, 7)]
    [Arguments(false, CompressionCodec.Gzip, 150, 1)]
    [Arguments(true, CompressionCodec.Snappy, 300, 13)]
    [Arguments(true, CompressionCodec.Zstd, 100, 64)]
    public async Task TheNestedDataset_RoundTrips_WhateverThePageAndBatchSizes(bool dictionary, CompressionCodec codec, int pageSize, int batchEntries)
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = dictionary, Compression = codec, Codecs = TestParquetFile.Registry, DataPageSize = pageSize };

        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows, options, batchEntries: batchEntries);

        await AssertFileHolds(bytes, AllRows);
    }

    [Test]
    public async Task TheNestedDataset_RoundTrips_InSeveralRowGroups()
    {
        var options = new ParquetWriterOptions { DataPageSize = 300 };

        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows, options, rowsPerGroup: 70, batchEntries: 11);

        await AssertFileHolds(bytes, AllRows, rowsPerGroup: 70);
    }

    [Test]
    public async Task ADictionaryThatOutgrowsItsLimit_FallsBackToPlain_AtARowBoundary()
    {
        var options = new ParquetWriterOptions { DataPageSize = 200, DictionaryPageSizeLimit = 64 };

        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows, options, batchEntries: 9);

        await AssertFileHolds(bytes, AllRows);
        var file = TestParquetFile.Parse(bytes);
        var encodings = file.DataPages(0, 7).Select(p => p.Header.DataPageHeader!.Encoding).ToList();
        await Assert.That(encodings.Distinct().Count()).IsEqualTo(2); // some dictionary pages, then plain pages
    }

    [Test]
    public async Task PagesNeverEndInTheMiddleOfARow_EvenWhenBatchesDo()
    {
        // Batches of one entry end every row after the first element, yet AssertStructure requires each page to start a row.
        var options = new ParquetWriterOptions { DataPageSize = 64, DictionaryEnabled = false };
        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows, options, batchEntries: 1);
        var file = TestParquetFile.Parse(bytes);

        file.AssertStructure();

        await Assert.That(file.DataPages(0, 1).Count).IsGreaterThan(5);
    }

    [Test]
    public async Task ARowLargerThanAPage_StaysInOnePage()
    {
        var schema = new ParquetSchema([NestedDataset.ListOf("l", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional))]);
        var rows = Enumerable.Range(0, 3)
            .Select(r => new StructValue { ["l"] = NestedDataset.List(Enumerable.Range(0, 5000).Select(i => (object?)(r * 10_000 + i)).ToArray()) })
            .ToArray();
        var options = new ParquetWriterOptions { DataPageSize = 100, DictionaryEnabled = false };

        var bytes = NestedFile.Write(schema, rows, options, batchEntries: 777);

        await AssertFileHolds(bytes, rows);
        await Assert.That(TestParquetFile.Parse(bytes).DataPages(0, 0).Count).IsEqualTo(3); // one page per row
    }

    [Test]
    public async Task TheRowCountAndEntryCount_AreReportedSeparately()
    {
        var schema = NestedDataset.Schema();
        var shredded = ReferenceShredder.Shred(schema, AllRows);
        var stream = new MemoryStream();
        using var writer = new ParquetFileWriter(stream, schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();

        var column = group.NextColumn();
        NestedFile.WriteColumn(column, shredded[0]);
        var scores = group.NextColumn();
        NestedFile.WriteColumn(scores, shredded[1]);

        await Assert.That(column.RowCount).IsEqualTo((long)NestedDataset.RowCount);
        await Assert.That(scores.RowCount).IsEqualTo((long)NestedDataset.RowCount);
        await Assert.That(scores.EntryCount).IsEqualTo((long)shredded[1].Definition.Count);
        await Assert.That(scores.EntryCount).IsGreaterThan(scores.RowCount);
    }

    [Test]
    public async Task OutputDoesNotDependOnHowTheBatchesAreSplit_WhenThereIsOnePage()
    {
        var reference = NestedFile.Write(NestedDataset.Schema(), AllRows);

        foreach (int batch in new[] { 1, 2, 3, 10, 99, 100_000 })
        {
            var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows, batchEntries: batch);

            await Assert.That(bytes.AsSpan().SequenceEqual(reference)).IsTrue().Because($"batches of {batch}");
        }
    }

    [Test]
    public async Task NullCounts_OfNestedColumns_CountEveryEntryWithoutAValue()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows);
        using var reader = ReadHelper.Open(bytes);
        var expected = ReferenceShredder.Shred(reader.Schema, AllRows);

        for (int column = 0; column < expected.Length; column++)
        {
            var statistics = reader.OpenRowGroup(0).GetStatistics(column)!;

            await Assert.That(statistics.NullCount).IsEqualTo((long)(expected[column].Definition.Count - expected[column].Values.Count)).Because(NestedDataset.ColumnNames[column]);
        }
    }

    [Test]
    public async Task Statistics_OfNestedColumns_CoverTheirValues()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), AllRows);
        using var reader = ReadHelper.Open(bytes);

        var statistics = reader.OpenRowGroup(0).GetStatistics(reader.Schema.FindColumn(["events", "list", "element", "count"])!.ColumnIndex)!;
        var counts = ReferenceShredder.Shred(reader.Schema, AllRows)[7].Values.Cast<int>().ToArray();

        await Assert.That(statistics.Min).IsEqualTo(counts.Min());
        await Assert.That(statistics.Max).IsEqualTo(counts.Max());
    }

    // ---- golden files ----

    [Test]
    [Arguments("golden-nested-plain.parquet", false)]
    [Arguments("golden-nested-dictionary.parquet", true)]
    public async Task TheWriterOutput_MatchesTheGoldenNestedFile_ByteForByte(string fixture, bool dictionary)
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = dictionary, DataPageSize = 512, CreatedBy = "ParquetNeo golden" };
        var actual = NestedFile.Write(NestedDataset.Schema(), AllRows, options, rowsPerGroup: 120);

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, fixture), actual);
            return;
        }

        var expected = File.ReadAllBytes(TestBytes.FixturePath(fixture));

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(expected));
        await AssertFileHolds(expected, AllRows, rowsPerGroup: 120);
    }
}
