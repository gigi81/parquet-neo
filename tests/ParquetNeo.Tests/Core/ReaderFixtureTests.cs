using ParquetNeo.Core;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core;

/// <summary>Reads files written by pyarrow (an independent implementation) and checks every value.</summary>
public class ReaderFixtureTests
{
    // ---- the 13-column dataset, written by pyarrow with the same formulas as SampleFile ----

    [Test]
    [Arguments("pyarrow-flat-required-v1.parquet", false, 1000)]
    [Arguments("pyarrow-flat-optional-v1.parquet", true, 1000)]
    [Arguments("pyarrow-flat-optional-v2.parquet", true, 1000)]
    [Arguments("pyarrow-flat-required-v2-snappy.parquet", false, 1000)]
    [Arguments("pyarrow-flat-optional-v1-smallpages.parquet", true, 3000)]
    [Arguments("pyarrow-flat-required-dict-v1.parquet", false, 1000)]
    [Arguments("pyarrow-flat-optional-dict-v1.parquet", true, 1000)]
    [Arguments("pyarrow-flat-optional-dict-v2.parquet", true, 1000)]
    [Arguments("pyarrow-flat-optional-dict-v1-snappy-smallpages.parquet", true, 3000)]
    [Arguments("pyarrow-flat-required-dict-fallback.parquet", false, 3000)]
    [Arguments("pyarrow-flat-optional-dict-legacy.parquet", true, 1000)]
    public async Task ThePyArrowSampleDataset_ReadsBackExactly(string fixture, bool optional, int rows)
    {
        using var reader = ReadHelper.OpenFixture(fixture);

        await Assert.That(reader.RowCount).IsEqualTo((long)rows);
        foreach (int batchSize in new[] { 1000, 37 })
        {
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                var actual = ReadHelper.ReadColumn(reader, 0, column, batchSize);

                await Assert.That(SampleFile.FirstDifference(actual, optional, 0, rows, column)).IsNull().Because($"{fixture} {SampleFile.ColumnNames[column]} batch {batchSize}");
            }
        }
    }

    [Test]
    [Arguments("pyarrow-flat-required-dict-v1.parquet")]
    [Arguments("pyarrow-flat-optional-dict-v2.parquet")]
    [Arguments("pyarrow-flat-optional-dict-v1-snappy-smallpages.parquet")]
    public async Task TheDictionaryFixtures_ReallyAreDictionaryEncoded(string fixture)
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath(fixture)));
        file.AssertStructureLoosely();

        // Booleans (column 4) are never dictionary encoded; every other column is.
        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var pages = file.Pages(0, column);
            bool dictionaryFirst = pages[0].Header.Type == PageType.DictionaryPage;
            bool indexed = pages.Skip(1).All(p => p.Header.Type == PageType.DataPage
                ? p.Header.DataPageHeader!.Encoding == ParquetEncoding.RleDictionary
                : p.Header.DataPageHeader?.Encoding == ParquetEncoding.RleDictionary
                  || p.Header.DataPageHeaderV2!.Encoding == ParquetEncoding.RleDictionary);

            await Assert.That(dictionaryFirst && indexed).IsEqualTo(column != 4).Because(SampleFile.ColumnNames[column]);
        }
    }

    [Test]
    public async Task TheFallbackFixture_SwitchesFromDictionaryPagesToPlainPages()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-required-dict-fallback.parquet")));

        // Column 1 (i64) has 3000 distinct values, which outgrow the 2000-byte dictionary limit.
        var encodings = file.DataPages(0, 1).Select(p => p.Header.DataPageHeader!.Encoding).ToList();

        await Assert.That(encodings[0]).IsEqualTo(ParquetEncoding.RleDictionary);
        await Assert.That(encodings[^1]).IsEqualTo(ParquetEncoding.Plain);
    }

    [Test]
    public async Task TheSmallPagesFixture_ReallyHasManyPages()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-v1-smallpages.parquet")));
        file.AssertStructureLoosely();

        await Assert.That(file.Pages(0, 0).Count).IsGreaterThan(3);
        await Assert.That(file.Pages(0, 5).Count).IsGreaterThan(3);
    }

    // ---- the PLAIN fixtures from step 3: nulls in mixed patterns, both boolean encodings ----

    [Test]
    [Arguments("pyarrow-plain-v1.parquet")]
    [Arguments("pyarrow-plain-v2.parquet")]
    public async Task PlainFixtures_ReadBackWhateverEncodingTheFileUses(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);

        (string Column, Func<int, bool> IsNull, Func<int, object> Value)[] expected =
        [
            ("dense_int", _ => false, i => i * 3 - 500),
            ("int32", i => i % 7 == 0, i => i * 3 - 500),
            ("int64", i => i >= 100 && i < 600, i => i * 1_000_003L - 7),
            ("float", i => i % 11 == 0, i => (float)(i * 0.5 - 3.25)),
            ("double", i => i % 13 == 0, i => i * 0.125 + 1e-3),
            ("flag", i => i % 17 == 0, i => i % 3 == 0 || i % 5 == 0), // PLAIN in v1, RLE in v2
            ("flag_rle", _ => false, i => i % 100 < 70), // RLE in both
            ("text", i => i % 9 == 0, i => System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat($"value-{i}", i % 4)))),
            ("fixed", i => i % 5 == 0, i => new[] { (byte)(i % 256), (byte)1, (byte)2, (byte)3 }),
        ];

        foreach (var (name, isNull, value) in expected)
        {
            var actual = ReadHelper.ReadColumn(reader, 0, reader.Schema.FindColumn([name])!.ColumnIndex, 300);

            await Assert.That(Differences(actual, 1000, isNull, value)).IsNull().Because(name);
        }
    }

    // ---- the extremes of every integer type ----

    [Test]
    [Arguments("pyarrow-int-extremes-plain-v1.parquet")]
    [Arguments("pyarrow-int-extremes-plain-v2.parquet")]
    [Arguments("pyarrow-int-extremes-dict-v1.parquet")]
    [Arguments("pyarrow-int-extremes-dict-v2.parquet")]
    public async Task IntegerExtremes_AreReadAsTheirPhysicalBits(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);

        // Unsigned values are stored in the signed physical type as the same bits: uint32 0xFFFFFFFF reads as -1.
        (string Column, object[] Expected)[] expected =
        [
            ("int8", [-128, -1, 0, 1, 127]),
            ("int16", [-32768, -1, 0, 1, 32767]),
            ("int32", [int.MinValue, -1, 0, 1, int.MaxValue]),
            ("int64", [long.MinValue, -1L, 0L, 1L, long.MaxValue]),
            ("uint8", [0, 1, 127, 128, 255]),
            ("uint16", [0, 1, 32767, 32768, 65535]),
            ("uint32", [0, 1, int.MaxValue, int.MinValue, -1]),
            ("uint64", [0L, 1L, long.MaxValue, long.MinValue, -1L]),
        ];

        foreach (var (column, values) in expected)
        {
            var actual = ReadHelper.ReadColumn(reader, 0, reader.Schema.FindColumn([column])!.ColumnIndex, 3);

            await Assert.That(actual.Select(SampleFile.Canonical)).IsEquivalentTo(values.Select(SampleFile.Canonical)).Because(column);
        }
    }

    // ---- every codec, as written by pyarrow ----

    [Test]
    [Arguments("snappy", 1)]
    [Arguments("snappy", 2)]
    [Arguments("gzip", 1)]
    [Arguments("gzip", 2)]
    [Arguments("brotli", 1)]
    [Arguments("brotli", 2)]
    [Arguments("zstd", 1)]
    [Arguments("zstd", 2)]
    public async Task CompressedPyArrowFiles_ReadBackExactly(string codec, int pageVersion)
    {
        using var reader = ReadHelper.OpenFixture($"pyarrow-codec-{codec}-v{pageVersion}.parquet");

        var squares = ReadHelper.ReadColumn(reader, 0, 0, 250);
        var ratios = ReadHelper.ReadColumn(reader, 0, 1, 250);

        await Assert.That(squares.Select(v => (long)v!)).IsEquivalentTo(Enumerable.Range(0, 1000).Select(i => (long)i * i));
        await Assert.That(ratios.Select(v => (double)v!)).IsEquivalentTo(Enumerable.Range(0, 1000).Select(i => i * 0.25));
    }

    [Test]
    public async Task ACodecThatIsNotRegistered_IsReportedWhenTheColumnIsOpened()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-codec-snappy-v1.parquet"));
        using var reader = new ParquetFileReader(new MemoryStream(bytes)); // default registry: no Snappy

        var exception = Assert.Throws<NotSupportedException>(() => reader.OpenRowGroup(0).OpenColumn(0));

        await Assert.That(exception.Message).Contains("ParquetNeo.Compression.Snappy");
    }

    // ---- the golden file written by the library itself ----

    [Test]
    [Arguments("golden-flat-uncompressed.parquet")]
    [Arguments("golden-flat-dictionary.parquet")]
    public async Task TheGoldenFiles_ReadBack(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);

        await Assert.That(ReadHelper.ReadColumn(reader, 0, 0, 3).Select(v => (long)v!)).IsEquivalentTo(new long[] { 1, 2, 3, 4, 5, 6, 7 });
        await Assert.That(ReadHelper.ReadColumn(reader, 0, 1, 3).Select(v => v is null ? null : System.Text.Encoding.UTF8.GetString((byte[])v)))
            .IsEquivalentTo(new[] { "alpha", null, "δelta", "alpha", null, "", "δelta" });
        await Assert.That(ReadHelper.ReadColumn(reader, 0, 2, 3)).IsEquivalentTo(new object?[] { 1.5, null, null, 1.5, 5.0, null, 5.0 });
        await Assert.That(ReadHelper.ReadColumn(reader, 0, 3, 3)).IsEquivalentTo(new object?[] { true, false, true, true, false, false, true });
        await Assert.That(ReadHelper.ReadColumn(reader, 0, 4, 3)).IsEquivalentTo(new object?[] { 18000, null, 18000, 18003, null, 18003, 18006 });
    }

    private static string? Differences(object?[] actual, int rows, Func<int, bool> isNull, Func<int, object> value)
    {
        if (actual.Length != rows)
        {
            return $"{actual.Length} rows, expected {rows}";
        }

        for (int i = 0; i < rows; i++)
        {
            string expected = isNull(i) ? "null" : SampleFile.Canonical(value(i));
            if (SampleFile.Canonical(actual[i]) != expected)
            {
                return $"row {i}: got {SampleFile.Canonical(actual[i])}, expected {expected}";
            }
        }

        return null;
    }
}
