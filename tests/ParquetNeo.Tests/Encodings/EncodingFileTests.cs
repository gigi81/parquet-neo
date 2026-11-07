using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Encodings;

public class EncodingFileTests
{
    private static async Task AssertColumns(byte[] bytes, bool optional, string where)
    {
        using var reader = ReadHelper.Open(bytes);
        for (int column = 0; column < EncodingDataset.Columns.Length; column++)
        {
            foreach (int batch in new[] { 1, 37, 5000 })
            {
                var actual = ReadHelper.ReadColumn(reader, 0, column, batch);

                string difference = "";
                if (actual.Length != EncodingDataset.RowCount)
                {
                    difference = $"{actual.Length} rows";
                }
                else
                {
                    for (int row = 0; row < actual.Length && difference.Length == 0; row++)
                    {
                        string expected = optional && EncodingDataset.IsNull(column, row) ? "null" : SampleFile.Canonical(EncodingDataset.Value(column, row));
                        if (SampleFile.Canonical(actual[row]) != expected)
                        {
                            difference = $"row {row}: got {SampleFile.Canonical(actual[row])}, expected {expected}";
                        }
                    }
                }

                await Assert.That(difference).IsEqualTo("").Because($"{where} {EncodingDataset.Columns[column].Name} batch {batch}");
            }
        }
    }

    // ---- files from pyarrow ----

    [Test]
    [Arguments("pyarrow-encodings-required-v1.parquet", false)]
    [Arguments("pyarrow-encodings-optional-v1.parquet", true)]
    [Arguments("pyarrow-encodings-optional-v2-smallpages.parquet", true)]
    [Arguments("pyarrow-encodings-required-v2-snappy.parquet", false)]
    public async Task ThePyArrowEncodingFiles_ReadBackExactly(string fixture, bool optional)
    {
        await AssertColumns(File.ReadAllBytes(TestBytes.FixturePath(fixture)), optional, fixture);
    }

    [Test]
    public async Task ThePyArrowEncodingFiles_UseTheEncodingsTheyShould()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath("pyarrow-encodings-optional-v2-smallpages.parquet")));

        for (int column = 0; column < EncodingDataset.Columns.Length; column++)
        {
            var pages = file.DataPages(0, column);
            await Assert.That(pages.All(p => p.Header.DataPageHeaderV2!.Encoding == EncodingDataset.Columns[column].Encoding)).IsTrue().Because(EncodingDataset.Columns[column].Name);
        }
    }

    [Test]
    public async Task TheSmallPagesFixture_ReallyHasManyPages()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath("pyarrow-encodings-optional-v2-smallpages.parquet")));

        // The columns that do not shrink (split_double) fill several 800-byte pages.
        await Assert.That(file.DataPages(0, 6).Count).IsGreaterThan(5);
    }

    // ---- files from our writer ----

    [Test]
    [Arguments(false, CompressionCodec.Uncompressed, 1 << 20, 3000)]
    [Arguments(true, CompressionCodec.Uncompressed, 1 << 20, 3000)]
    [Arguments(true, CompressionCodec.Uncompressed, 400, 211)]
    [Arguments(false, CompressionCodec.Gzip, 300, 64)]
    [Arguments(true, CompressionCodec.Zstd, 700, 1000)]
    public async Task OurEncodedFiles_RoundTrip(bool optional, CompressionCodec codec, int pageSize, int batchRows)
    {
        var options = new ParquetWriterOptions
        {
            DictionaryEnabled = false,
            ColumnEncodings = EncodingDataset.Encodings(),
            Compression = codec,
            Codecs = TestParquetFile.Registry,
            DataPageSize = pageSize,
        };

        var bytes = EncodingDataset.Write(optional, options, batchRows);

        TestParquetFile.Parse(bytes).AssertStructure();
        await AssertColumns(bytes, optional, $"{codec} pages of {pageSize}");
    }

    [Test]
    public async Task EveryColumn_IsWrittenWithItsEncoding_AndListsOnlyEncodingsItUses()
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = false, ColumnEncodings = EncodingDataset.Encodings(), DataPageSize = 500 };
        var file = TestParquetFile.Parse(EncodingDataset.Write(optional: true, options));

        for (int column = 0; column < EncodingDataset.Columns.Length; column++)
        {
            var chunk = file.Chunk(0, column);
            string name = EncodingDataset.Columns[column].Name;

            await Assert.That(file.DataPages(0, column).All(p => p.Header.DataPageHeader!.Encoding == EncodingDataset.Columns[column].Encoding)).IsTrue().Because(name);
            await Assert.That(chunk.Encodings).IsEquivalentTo(new[] { EncodingDataset.Columns[column].Encoding, ParquetEncoding.Rle }).Because(name);
        }
    }

    [Test]
    public async Task AColumnWithoutAChosenEncoding_StaysPlain()
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = false };
        var file = TestParquetFile.Parse(EncodingDataset.Write(optional: false, options));

        await Assert.That(Enumerable.Range(0, EncodingDataset.Columns.Length).All(c => file.Chunk(0, c).Encodings.SequenceEqual(new[] { ParquetEncoding.Plain }))).IsTrue();
    }

    [Test]
    public async Task TheChosenEncoding_TakesOverWhenTheDictionaryIsGivenUp()
    {
        var options = new ParquetWriterOptions
        {
            ColumnEncodings = EncodingDataset.Encodings(),
            DictionaryPageSizeLimit = 200,
            DataPageSize = 600,
        };
        var bytes = EncodingDataset.Write(optional: false, options);
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        // delta_int64 has 3000 distinct values: dictionary pages first, then delta pages.
        var encodings = file.DataPages(0, 2).Select(p => p.Header.DataPageHeader!.Encoding).ToList();
        await Assert.That(encodings[0]).IsEqualTo(ParquetEncoding.RleDictionary);
        await Assert.That(encodings[^1]).IsEqualTo(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(file.Chunk(0, 2).Encodings).Contains(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(file.Chunk(0, 2).Encodings).Contains(ParquetEncoding.Plain); // the dictionary page
        await AssertColumns(bytes, false, "fallback");
    }

    [Test]
    public async Task DeltaEncodingShrinksSteadyIntegers_AndByteStreamSplitHelpsCompression()
    {
        var plain = EncodingDataset.Write(optional: false, new ParquetWriterOptions { DictionaryEnabled = false });
        var encoded = EncodingDataset.Write(optional: false, new ParquetWriterOptions { DictionaryEnabled = false, ColumnEncodings = EncodingDataset.Encodings() });

        var plainFile = TestParquetFile.Parse(plain);
        var encodedFile = TestParquetFile.Parse(encoded);

        // The steady integers, and the strings that share prefixes, take much less room.
        await Assert.That(encodedFile.Chunk(0, 0).TotalUncompressedSize).IsLessThan(plainFile.Chunk(0, 0).TotalUncompressedSize / 3);
        await Assert.That(encodedFile.Chunk(0, 4).TotalUncompressedSize).IsLessThan(plainFile.Chunk(0, 4).TotalUncompressedSize / 50);
        await Assert.That(encodedFile.Chunk(0, 9).TotalUncompressedSize).IsLessThan(plainFile.Chunk(0, 9).TotalUncompressedSize / 2);
        // Byte stream split keeps the size.
        await Assert.That(encodedFile.Chunk(0, 5).TotalUncompressedSize).IsEqualTo(plainFile.Chunk(0, 5).TotalUncompressedSize);
    }

    [Test]
    public async Task AnEncodingTheColumnTypeCannotUse_IsReportedWhenReading()
    {
        var schema = new ParquetNeo.Schema.ParquetSchema([new ParquetNeo.Schema.PrimitiveNode("d", PhysicalType.Double, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { DictionaryEnabled = false }, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(new double[100]);
        }

        // Claim the PLAIN doubles are delta encoded: a double column cannot be.
        var bytes = FooterEditor.PatchPageHeader(stream.ToArray(), 0, h => h.DataPageHeader!.Encoding = ParquetEncoding.DeltaBinaryPacked);
        using var reader = ReadHelper.Open(bytes);

        var exception = Assert.Throws<CorruptColumnException>(() => ReadHelper.ReadColumn(reader, 0, 0, 10));

        await Assert.That(exception.Message).Contains("cannot use");
    }

    // ---- choosing encodings ----

    [Test]
    public async Task ChoosingAnEncodingTheTypeCannotUse_IsRejectedWhenTheWriterIsCreated()
    {
        var schema = EncodingDataset.Schema(optional: false);

        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), schema, new ParquetWriterOptions { ColumnEncodings = new Dictionary<string, ParquetEncoding> { ["split_float"] = ParquetEncoding.DeltaBinaryPacked } })).Throws<ArgumentException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), EncodingDataset.Schema(false), new ParquetWriterOptions { ColumnEncodings = new Dictionary<string, ParquetEncoding> { ["delta_int32"] = ParquetEncoding.DeltaByteArray } })).Throws<ArgumentException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), EncodingDataset.Schema(false), new ParquetWriterOptions { ColumnEncodings = new Dictionary<string, ParquetEncoding> { ["delta_int32"] = ParquetEncoding.RleDictionary } })).Throws<ArgumentException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), EncodingDataset.Schema(false), new ParquetWriterOptions { ColumnEncodings = new Dictionary<string, ParquetEncoding> { ["nope"] = ParquetEncoding.Plain } })).Throws<ArgumentException>();
    }

    // ---- golden file ----

    [Test]
    public async Task TheWriterOutput_MatchesTheGoldenEncodingFile_ByteForByte()
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = false, ColumnEncodings = EncodingDataset.Encodings(), DataPageSize = 1024, CreatedBy = "ParquetNeo golden" };
        var actual = EncodingDataset.Write(optional: true, options);

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-encodings.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-encodings.parquet"))));
    }
}
