using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class DictionaryModeTests
{
    private static byte[] WriteLongs(long[] values, ParquetWriterOptions options, int batchSize = int.MaxValue)
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int64, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            var column = group.NextColumn();
            for (int start = 0; start < values.Length; start += batchSize)
            {
                column.WriteBatch(values.AsSpan(start, Math.Min(batchSize, values.Length - start)));
            }
        }

        return stream.ToArray();
    }

    private static ParquetWriterOptions Mode(DictionaryMode mode) =>
        new() { ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["c"] = mode } };

    private static long[] Unique(int count) => Enumerable.Range(0, count).Select(i => i * 7_919L).ToArray();

    [Test]
    public async Task AnAdaptiveColumnOfUniqueValues_DropsItsDictionaryAfterTheFirstValues()
    {
        var values = Unique(200_000);
        var bytes = WriteLongs(values, Mode(DictionaryMode.Adaptive));
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        var encodings = file.DataPages(0, 0).Select(p => p.Header.DataPageHeader!.Encoding).ToList();
        int dictionaryEntries = file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues;

        // 32 KiB of values is 4096 longs: that is all the dictionary ever held.
        await Assert.That(dictionaryEntries).IsEqualTo(4096);
        await Assert.That(encodings[0]).IsEqualTo(ParquetEncoding.RleDictionary);
        await Assert.That(encodings.Skip(1).All(e => e == ParquetEncoding.Plain)).IsTrue();
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 5000).Select(v => (long)v!)).IsEquivalentTo(values);
    }

    [Test]
    public async Task AnAdaptiveColumnThatRepeats_KeepsItsDictionary_AndTheSameBytesAsStandard()
    {
        long[] values = Enumerable.Range(0, 200_000).Select(i => 1_000_000_000_000L + i % 10).ToArray();

        var adaptive = WriteLongs(values, Mode(DictionaryMode.Adaptive));
        var standard = WriteLongs(values, new ParquetWriterOptions());

        await Assert.That(adaptive.AsSpan().SequenceEqual(standard)).IsTrue();
    }

    [Test]
    public async Task TheDecisionIsTheSame_WhateverTheBatchSize()
    {
        var values = Unique(100_000);

        foreach (int batchSize in new[] { 1, 100, 4096, 70_000 })
        {
            var file = TestParquetFile.Parse(WriteLongs(values, Mode(DictionaryMode.Adaptive), batchSize));
            file.AssertStructure();

            await Assert.That(file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(4096).Because($"batches of {batchSize}");
            await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(values.Select(v => (long?)v)).Because($"batches of {batchSize}");
        }
    }

    [Test]
    public async Task AnAdaptiveColumnShorterThanTheSample_IsDecidedLikeAStandardOne()
    {
        var values = Unique(1000);

        var adaptive = WriteLongs(values, Mode(DictionaryMode.Adaptive));
        var standard = WriteLongs(values, new ParquetWriterOptions());

        await Assert.That(adaptive.AsSpan().SequenceEqual(standard)).IsTrue();
    }

    [Test]
    public async Task ADisabledColumn_IsWrittenPlain()
    {
        var values = Enumerable.Range(0, 50_000).Select(i => (long)(i % 5)).ToArray();
        var bytes = WriteLongs(values, Mode(DictionaryMode.Disabled));
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        await Assert.That(file.Chunk(0, 0).DictionaryPageOffset).IsNull();
        await Assert.That(file.Pages(0, 0).All(p => p.Header.Type == PageType.DataPage)).IsTrue();
        await Assert.That(file.DataPages(0, 0).All(p => p.Header.DataPageHeader!.Encoding == ParquetEncoding.Plain)).IsTrue();
        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(values.Select(v => (long?)v));
    }

    [Test]
    public async Task TurningDictionariesOff_StillWinsOverAColumnsMode()
    {
        var options = new ParquetWriterOptions { DictionaryEnabled = false, ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["c"] = DictionaryMode.Standard } };

        var file = TestParquetFile.Parse(WriteLongs(Unique(1000), options));

        await Assert.That(file.Chunk(0, 0).DictionaryPageOffset).IsNull();
    }

    [Test]
    public async Task AColumnThatDoesNotExist_IsRefusedWhenTheWriterIsCreated()
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int64, FieldRepetitionType.Required)]);
        var options = new ParquetWriterOptions { ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["nope"] = DictionaryMode.Disabled } };

        var exception = Assert.Throws<ArgumentException>(() => new ParquetFileWriter(new MemoryStream(), schema, options));

        await Assert.That(exception.Message).Contains("nope");
    }

    [Test]
    public async Task AModeThatIsNotDefined_IsRefusedWhenTheWriterIsCreated()
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int64, FieldRepetitionType.Required)]);
        var options = new ParquetWriterOptions { ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["c"] = (DictionaryMode)7 } };

        var exception = Assert.Throws<ArgumentException>(() => new ParquetFileWriter(new MemoryStream(), schema, options));

        await Assert.That(exception.Message).Contains("'c'");
    }

    private static ParquetWriterOptions Limits(int? writerLimit, int? columnLimit) =>
        new()
        {
            DataPageSize = 600,
            DictionaryPageSizeLimit = writerLimit ?? 1024 * 1024,
            ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["c"] = new DictionarySettings { SizeLimit = columnLimit } },
        };

    private static IEnumerable<ParquetEncoding> DataEncodings(byte[] bytes) =>
        TestParquetFile.Parse(bytes).DataPages(0, 0).Select(p => p.Header.DataPageHeader!.Encoding);

    [Test]
    public async Task ASizeLimitOfAColumn_ReplacesTheLimitOfTheWriter_WhenHigher()
    {
        var values = Unique(5000);

        // The limit of the writer would drop the dictionary after a few hundred values; the column's keeps it to the end.
        var kept = DataEncodings(WriteLongs(values, Limits(writerLimit: 2000, columnLimit: 1024 * 1024), batchSize: 100)).ToList();
        var dropped = DataEncodings(WriteLongs(values, Limits(writerLimit: 2000, columnLimit: null), batchSize: 100)).ToList();

        await Assert.That(kept.All(e => e == ParquetEncoding.RleDictionary)).IsTrue();
        await Assert.That(dropped.Last()).IsEqualTo(ParquetEncoding.Plain);
    }

    [Test]
    public async Task ASizeLimitOfAColumn_ReplacesTheLimitOfTheWriter_WhenLower()
    {
        var values = Unique(5000);

        var bytes = WriteLongs(values, Limits(writerLimit: null, columnLimit: 2000), batchSize: 100);
        TestParquetFile.Parse(bytes).AssertStructure();

        await Assert.That(DataEncodings(bytes).Last()).IsEqualTo(ParquetEncoding.Plain);
        await Assert.That(TestParquetFile.Parse(bytes).ReadColumn<long>(0, 0)).IsEquivalentTo(values.Select(v => (long?)v));
    }

    [Test]
    public async Task ASizeLimitBelowOne_IsRefusedWhenTheWriterIsCreated()
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int64, FieldRepetitionType.Required)]);

        var exception = Assert.Throws<ArgumentException>(() => new ParquetFileWriter(new MemoryStream(), schema, Limits(null, 0)));

        await Assert.That(exception.Message).Contains("size limit");
    }
}
