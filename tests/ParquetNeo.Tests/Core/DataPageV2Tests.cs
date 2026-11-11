using ParquetNeo.Core;
using ParquetNeo.Tests.Core.Nested;
using ParquetNeo.Tests.Encodings;

namespace ParquetNeo.Tests.Core;

public class DataPageV2Tests
{
    private static ParquetWriterOptions V2(CompressionCodec codec = CompressionCodec.Uncompressed, int pageSize = 1 << 20, bool dictionary = true) => new()
    {
        DataPageVersion = DataPageVersion.V2,
        Compression = codec,
        Codecs = TestParquetFile.Registry,
        DataPageSize = pageSize,
        DictionaryEnabled = dictionary,
    };

    [Test]
    [Arguments(false, CompressionCodec.Uncompressed, 1 << 20, true)]
    [Arguments(true, CompressionCodec.Uncompressed, 500, true)]
    [Arguments(true, CompressionCodec.Gzip, 700, false)]
    [Arguments(false, CompressionCodec.Snappy, 900, true)]
    [Arguments(true, CompressionCodec.Zstd, 1200, false)]
    [Arguments(true, CompressionCodec.Lz4Raw, 3000, true)]
    [Arguments(false, CompressionCodec.Brotli, 600, false)]
    public async Task TheSampleDataset_RoundTripsThroughDataPagesV2(bool optional, CompressionCodec codec, int pageSize, bool dictionary)
    {
        var bytes = SampleFile.Build(optional, 2000, groups: 2, options: V2(codec, pageSize, dictionary));
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        using var reader = ReadHelper.Open(bytes);

        for (int group = 0; group < 2; group++)
        {
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                await Assert.That(SampleFile.FirstDifference(file, optional, group, group * 1000, 1000, column)).IsNull().Because($"mini reader, group {group}");
                foreach (int batch in new[] { 1000, 7 })
                {
                    var actual = ReadHelper.ReadColumn(reader, group, column, batch);
                    await Assert.That(SampleFile.FirstDifference(actual, optional, group * 1000, 1000, column)).IsNull().Because($"{codec} group {group} {SampleFile.ColumnNames[column]} batch {batch}");
                }
            }
        }
    }

    [Test]
    public async Task EveryDataPage_IsAV2Page_WithItsCountsAndLevelLengths()
    {
        var bytes = SampleFile.Build(optional: true, rows: 1500, options: V2(pageSize: 400, dictionary: false));
        var file = TestParquetFile.Parse(bytes);

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            string name = SampleFile.ColumnNames[column];
            var pages = file.DataPages(0, column);
            await Assert.That(pages.All(p => p.Header.Type == PageType.DataPageV2 && p.Header.DataPageHeader is null)).IsTrue().Because(name);
            long rows = 0;
            long nulls = 0;
            foreach (var page in pages)
            {
                var info = page.Header.DataPageHeaderV2!;
                rows += info.NumRows;
                nulls += info.NumNulls;
                await Assert.That(info.NumRows).IsEqualTo(info.NumValues).Because(name); // not repeated: every entry is a row
                await Assert.That(info.RepetitionLevelsByteLength).IsEqualTo(0).Because(name);
                await Assert.That(info.DefinitionLevelsByteLength).IsGreaterThan(0).Because(name);
                await Assert.That(info.IsCompressed).IsFalse().Because(name);
                await Assert.That(page.Header.CompressedPageSize).IsEqualTo(page.Header.UncompressedPageSize).Because(name);
            }

            await Assert.That(rows).IsEqualTo(1500L).Because(name);
            await Assert.That(nulls).IsEqualTo((long)Enumerable.Range(0, 1500).Count(i => SampleFile.IsNull(column, i))).Because(name);
        }
    }

    [Test]
    public async Task ARequiredColumn_HasNoLevelBytes()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: false, rows: 600, options: V2(dictionary: false)));

        var info = file.DataPages(0, 1).Single().Header.DataPageHeaderV2!;

        await Assert.That(info.DefinitionLevelsByteLength).IsEqualTo(0);
        await Assert.That(info.NumNulls).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTheValuesAreCompressed_AndTheLevelsStayReadableInTheStoredPage()
    {
        var bytes = SampleFile.Build(optional: true, rows: 800, options: V2(CompressionCodec.Gzip, dictionary: false));
        var file = TestParquetFile.Parse(bytes);

        foreach (var page in file.DataPages(0, 5)) // the strings: compressible
        {
            var info = page.Header.DataPageHeaderV2!;
            int levelBytes = info.RepetitionLevelsByteLength + info.DefinitionLevelsByteLength;
            var stored = bytes.AsSpan((int)page.Offset + page.HeaderLength, page.Header.CompressedPageSize);
            var levels = new int[info.NumValues];

            // The levels decode straight from the stored page, without decompressing it.
            new ParquetNeo.Encodings.RleBitPackedDecoder(stored[..levelBytes], 1).ReadValues(levels);

            await Assert.That(info.IsCompressed).IsTrue();
            await Assert.That(levels.Count(l => l == 1)).IsEqualTo(info.NumValues - info.NumNulls);
            await Assert.That(page.Header.CompressedPageSize).IsLessThan(page.Header.UncompressedPageSize);
            await Assert.That(page.Header.UncompressedPageSize - levelBytes).IsGreaterThan(page.Header.CompressedPageSize - levelBytes);
        }
    }

    [Test]
    public async Task TheChunkStatistics_NameTheV2PageType()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: true, rows: 500, options: V2(dictionary: true)));

        var stats = file.Chunk(0, 0).EncodingStats!;

        await Assert.That(stats.Where(s => s.PageType != PageType.DictionaryPage).All(s => s.PageType == PageType.DataPageV2)).IsTrue();
        await Assert.That(stats.Any(s => s.PageType == PageType.DictionaryPage)).IsTrue();
    }

    // ---- nested columns, encodings, dictionaries ----

    [Test]
    [Arguments(CompressionCodec.Uncompressed, 200, true, 7)]
    [Arguments(CompressionCodec.Snappy, 300, false, 1)]
    [Arguments(CompressionCodec.Zstd, 1 << 20, true, 64)]
    public async Task TheNestedDataset_RoundTripsThroughDataPagesV2(CompressionCodec codec, int pageSize, bool dictionary, int batchEntries)
    {
        var rows = NestedDataset.Rows().ToArray();
        var bytes = NestedFile.Write(NestedDataset.Schema(), rows, V2(codec, pageSize, dictionary), batchEntries: batchEntries);
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure(); // includes: every page starts a row
        using var reader = ReadHelper.Open(bytes);
        var expected = ReferenceShredder.Shred(file.Schema, rows);

        for (int c = 0; c < expected.Length; c++)
        {
            string name = NestedDataset.ColumnNames[c];
            var mini = file.ReadEntries(0, c);
            var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, c, 33);

            await Assert.That(mini.Definition).IsEquivalentTo(expected[c].Definition).Because("mini reader, " + name);
            await Assert.That(mini.Repetition).IsEquivalentTo(expected[c].Repetition).Because("mini reader, " + name);
            await Assert.That(definition).IsEquivalentTo(expected[c].Definition).Because(name);
            await Assert.That(repetition).IsEquivalentTo(expected[c].Repetition).Because(name);
            await Assert.That(values.Select(NestedReaderTests.Describe).SequenceEqual(expected[c].Values.Select(NestedReaderTests.Describe))).IsTrue().Because(name);
        }
    }

    [Test]
    public async Task ARepeatedColumn_StatesRowsAndRepetitionLevelLengths_InItsPages()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), NestedDataset.Rows().ToArray(), V2(pageSize: 150, dictionary: false));
        var file = TestParquetFile.Parse(bytes);

        long rows = 0;
        foreach (var page in file.DataPages(0, 1)) // scores.list.element
        {
            var info = page.Header.DataPageHeaderV2!;
            rows += info.NumRows;
            await Assert.That(info.RepetitionLevelsByteLength).IsGreaterThan(0);
            await Assert.That(info.NumRows).IsLessThanOrEqualTo(info.NumValues);
        }

        await Assert.That(rows).IsEqualTo((long)NestedDataset.RowCount);
        await Assert.That(file.DataPages(0, 1).Count).IsGreaterThan(3);
    }

    [Test]
    public async Task TheEncodings_WorkInDataPagesV2()
    {
        var options = new ParquetWriterOptions
        {
            DataPageVersion = DataPageVersion.V2,
            DictionaryEnabled = false,
            ColumnEncodings = EncodingDataset.Encodings(),
            Compression = CompressionCodec.Gzip,
            DataPageSize = 600,
        };
        var bytes = EncodingDataset.Write(optional: true, options);
        TestParquetFile.Parse(bytes).AssertStructure();
        using var reader = ReadHelper.Open(bytes);

        for (int column = 0; column < EncodingDataset.Columns.Length; column++)
        {
            var actual = ReadHelper.ReadColumn(reader, 0, column, 100);
            for (int row = 0; row < actual.Length; row++)
            {
                string expected = EncodingDataset.IsNull(column, row) ? "null" : SampleFile.Canonical(EncodingDataset.Value(column, row));
                await Assert.That(SampleFile.Canonical(actual[row])).IsEqualTo(expected).Because($"{EncodingDataset.Columns[column].Name} row {row}");
            }
        }
    }

    // ---- options ----

    [Test]
    public async Task TheDefaultIsV1_AndAnUnknownVersionIsRejected()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: true, rows: 100));

        await Assert.That(file.DataPages(0, 0).All(p => p.Header.Type == PageType.DataPage)).IsTrue();
        await Assert.That(new ParquetWriterOptions().DataPageVersion).IsEqualTo(DataPageVersion.V1);
        await Assert.That(() => SampleFile.Build(true, 10, options: new ParquetWriterOptions { DataPageVersion = (DataPageVersion)7 })).Throws<ArgumentOutOfRangeException>();
    }

    // ---- golden files ----

    [Test]
    [Arguments("golden-nested-v2.parquet")]
    public async Task TheWriterOutput_MatchesTheGoldenNestedV2File_ByteForByte(string fixture)
    {
        var options = new ParquetWriterOptions { DataPageVersion = DataPageVersion.V2, DataPageSize = 512, CreatedBy = "ParquetNeo golden" };
        var actual = NestedFile.Write(NestedDataset.Schema(), NestedDataset.Rows().ToArray(), options, rowsPerGroup: 120);

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, fixture), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath(fixture))));
    }

    [Test]
    [Arguments("golden-encodings-v2.parquet")]
    public async Task TheWriterOutput_MatchesTheGoldenEncodingsV2File_ByteForByte(string fixture)
    {
        var options = new ParquetWriterOptions
        {
            DataPageVersion = DataPageVersion.V2,
            DictionaryEnabled = false,
            ColumnEncodings = EncodingDataset.Encodings(),
            DataPageSize = 1024,
            CreatedBy = "ParquetNeo golden",
        };
        var actual = EncodingDataset.Write(optional: true, options);

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, fixture), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath(fixture))));
    }
}
