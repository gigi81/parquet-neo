using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class DictionaryTests
{
    private static ParquetWriterOptions Options(CompressionCodec codec = CompressionCodec.Uncompressed, int pageSize = 1024 * 1024, int dictionaryLimit = 1024 * 1024) =>
        new() { Compression = codec, Codecs = TestParquetFile.Registry, DataPageSize = pageSize, DictionaryPageSizeLimit = dictionaryLimit };

    private static byte[] WriteColumn<T>(PhysicalType type, FieldRepetitionType repetition, IEnumerable<T[]> batches, ParquetWriterOptions options, int[][]? levels = null, int? typeLength = null)
        where T : unmanaged
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", type, repetition, typeLength: typeLength)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            var column = group.NextColumn();
            int i = 0;
            foreach (var batch in batches)
            {
                var level = levels?[i++] ?? [];
                switch (batch)
                {
                    case int[] ints: column.WriteBatch(ints, level); break;
                    case long[] longs: column.WriteBatch(longs, level); break;
                    case float[] floats: column.WriteBatch(floats, level); break;
                    case double[] doubles: column.WriteBatch(doubles, level); break;
                    default: throw new NotSupportedException();
                }
            }
        }

        return stream.ToArray();
    }

    // ---- the sample dataset, every type, many page shapes ----

    [Test]
    [Arguments(false, CompressionCodec.Uncompressed, 1024 * 1024)]
    [Arguments(true, CompressionCodec.Uncompressed, 1024 * 1024)]
    [Arguments(true, CompressionCodec.Uncompressed, 500)]
    [Arguments(false, CompressionCodec.Gzip, 700)]
    [Arguments(true, CompressionCodec.Snappy, 900)]
    [Arguments(true, CompressionCodec.Zstd, 1200)]
    [Arguments(false, CompressionCodec.Lz4Raw, 3000)]
    [Arguments(true, CompressionCodec.Brotli, 600)]
    public async Task TheSampleDataset_RoundTripsThroughDictionaries(bool optional, CompressionCodec codec, int pageSize)
    {
        var bytes = SampleFile.Build(optional, 2000, groups: 2, options: Options(codec, pageSize));
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
                    await Assert.That(SampleFile.FirstDifference(actual, optional, group * 1000, 1000, column)).IsNull().Because($"{codec} group {group} batch {batch}");
                }
            }
        }
    }

    [Test]
    public async Task EveryColumnExceptBooleans_StartsWithADictionaryPage()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: true, rows: 500));

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var chunk = file.Chunk(0, column);
            bool boolean = file.Schema.Columns[column].Type == PhysicalType.Boolean;

            await Assert.That(chunk.DictionaryPageOffset is not null).IsEqualTo(!boolean).Because(SampleFile.ColumnNames[column]);
            await Assert.That(chunk.Encodings.Contains(ParquetEncoding.RleDictionary)).IsEqualTo(!boolean).Because(SampleFile.ColumnNames[column]);
        }
    }

    [Test]
    public async Task TurningDictionariesOff_WritesPlainPagesOnly()
    {
        var bytes = SampleFile.Build(optional: true, rows: 500, options: new ParquetWriterOptions { DictionaryEnabled = false });
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var chunk = file.Chunk(0, column);

            await Assert.That(chunk.DictionaryPageOffset).IsNull();
            await Assert.That(chunk.Encodings.Contains(ParquetEncoding.RleDictionary)).IsFalse();
            await Assert.That(file.Pages(0, column).All(p => p.Header.Type == PageType.DataPage)).IsTrue();
        }
    }

    [Test]
    public async Task TheOutput_IsDeterministic()
    {
        var first = SampleFile.Build(optional: true, rows: 3000, options: Options(CompressionCodec.Gzip, 1000));
        var second = SampleFile.Build(optional: true, rows: 3000, options: Options(CompressionCodec.Gzip, 1000));

        await Assert.That(first.AsSpan().SequenceEqual(second)).IsTrue();
    }

    // ---- size ----

    [Test]
    public async Task RepetitiveData_ShrinksALot()
    {
        long[] values = Enumerable.Range(0, 100_000).Select(i => 1_000_000_000_000L + i % 10).ToArray();
        var withDictionary = WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, [values], Options());
        var plain = WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, [values], new ParquetWriterOptions { DictionaryEnabled = false });

        await Assert.That(withDictionary.Length).IsLessThan(plain.Length / 10);
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(withDictionary), 0, 0, 4096).Select(v => (long)v!)).IsEquivalentTo(values);
    }

    // ---- falling back to PLAIN ----

    [Test]
    public async Task ADictionaryThatGrowsTooBig_FallsBackToPlainPages()
    {
        // 5000 distinct values arrive in batches of 100; the 2000-byte limit is crossed after a few hundred.
        var batches = Enumerable.Range(0, 50).Select(b => Enumerable.Range(b * 100, 100).Select(i => (long)i * 7_919).ToArray()).ToArray();
        var bytes = WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, batches, Options(pageSize: 600, dictionaryLimit: 2000));
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        var chunk = file.Chunk(0, 0);
        var encodings = file.DataPages(0, 0).Select(p => p.Header.DataPageHeader!.Encoding).ToList();

        await Assert.That(chunk.DictionaryPageOffset).IsNotNull();
        await Assert.That(encodings[0]).IsEqualTo(ParquetEncoding.RleDictionary);
        await Assert.That(encodings[^1]).IsEqualTo(ParquetEncoding.Plain);
        await Assert.That(encodings.SkipWhile(e => e == ParquetEncoding.RleDictionary).All(e => e == ParquetEncoding.Plain)).IsTrue();
        await Assert.That(chunk.Encodings).Contains(ParquetEncoding.RleDictionary);
        await Assert.That(chunk.Encodings).Contains(ParquetEncoding.Plain);
        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(batches.SelectMany(b => b).Select(v => (long?)v));
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 333).Select(v => (long)v!)).IsEquivalentTo(batches.SelectMany(b => b));
    }

    [Test]
    public async Task TheDictionaryIsNotGrownAfterTheFallback()
    {
        var batches = Enumerable.Range(0, 50).Select(b => Enumerable.Range(b * 100, 100).Select(i => (long)i).ToArray()).ToArray();
        var bytes = WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, batches, Options(pageSize: 600, dictionaryLimit: 2000));
        var file = TestParquetFile.Parse(bytes);

        int dictionaryEntries = file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues;

        // The limit is checked after each batch of 100, so the dictionary stops growing within one batch of it.
        await Assert.That(dictionaryEntries).IsGreaterThan(0).And.IsLessThanOrEqualTo(2000 / 8 + 100);
    }

    [Test]
    public async Task AColumnWhoseFirstBatchIsAlreadyTooBig_StillRoundTrips()
    {
        long[] values = Enumerable.Range(0, 10_000).Select(i => (long)i * 31).ToArray();
        var bytes = WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, [values], Options(dictionaryLimit: 100));
        TestParquetFile.Parse(bytes).AssertStructure();

        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 1000).Select(v => (long)v!)).IsEquivalentTo(values);
    }

    [Test]
    public async Task ATinyLimit_StillWritesAValidFile_ForEveryType()
    {
        var bytes = SampleFile.Build(optional: true, rows: 1500, options: Options(pageSize: 400, dictionaryLimit: 64));
        TestParquetFile.Parse(bytes).AssertStructure();
        using var reader = ReadHelper.Open(bytes);

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            await Assert.That(SampleFile.FirstDifference(ReadHelper.ReadColumn(reader, 0, column, 100), true, 0, 1500, column)).IsNull().Because(SampleFile.ColumnNames[column]);
        }
    }

    // ---- index widths ----

    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(3, 2)]
    [Arguments(4, 2)]
    [Arguments(5, 3)]
    [Arguments(256, 8)]
    [Arguments(257, 9)]
    [Arguments(65_536, 16)]
    [Arguments(65_537, 17)]
    public async Task TheIndexBitWidth_FitsTheDictionarySize(int distinct, int expectedWidth)
    {
        int[] values = Enumerable.Range(0, distinct * 2).Select(i => i % distinct).ToArray();
        var bytes = WriteColumn(PhysicalType.Int32, FieldRepetitionType.Required, [values], Options());
        var file = TestParquetFile.Parse(bytes);
        var page = file.DataPages(0, 0)[0];

        await Assert.That(bytes[page.Offset + page.HeaderLength]).IsEqualTo((byte)expectedWidth);
        await Assert.That(file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(distinct);
        await Assert.That(file.ReadColumn<int>(0, 0)).IsEquivalentTo(values.Select(v => (int?)v));
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 5000).Select(v => (int)v!)).IsEquivalentTo(values);
    }

    [Test]
    public async Task TheIndexWidthGrows_AsTheDictionaryGrowsAcrossPages()
    {
        // Each 40-byte page holds 10 ints: the first pages see 3 distinct values (2 bits), the later ones hundreds (9+ bits).
        var batches = new List<int[]>();
        for (int b = 0; b < 5; b++)
        {
            batches.Add(Enumerable.Range(0, 10).Select(i => i % 3).ToArray());
        }

        for (int b = 0; b < 100; b++)
        {
            batches.Add(Enumerable.Range(0, 10).Select(i => 3 + b * 10 + i).ToArray());
        }

        var bytes = WriteColumn(PhysicalType.Int32, FieldRepetitionType.Required, batches, Options(pageSize: 40));
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        var widths = file.DataPages(0, 0).Select(p => (int)bytes[p.Offset + p.HeaderLength]).ToList();

        await Assert.That(widths[0]).IsEqualTo(2);
        await Assert.That(widths[^1]).IsGreaterThanOrEqualTo(10);
        await Assert.That(widths.Zip(widths.Skip(1), (a, b) => b >= a).All(x => x)).IsTrue();
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 77).Select(v => (int)v!)).IsEquivalentTo(batches.SelectMany(b => b));
    }

    // ---- values that must stay distinct or exact ----

    [Test]
    public async Task FloatingPointValues_AreKeyedByTheirBits()
    {
        double[] doubles =
        [
            0.0, -0.0, 0.0, -0.0, double.NaN, BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001), double.NaN,
            double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, double.MaxValue, double.MinValue,
        ];
        float[] floats =
        [
            0f, -0f, 0f, -0f, float.NaN, BitConverter.Int32BitsToSingle(0x7FC0_0001), float.NaN,
            float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, float.MaxValue, float.MinValue,
        ];

        var doubleBytes = WriteColumn(PhysicalType.Double, FieldRepetitionType.Required, [doubles], Options());
        var floatBytes = WriteColumn(PhysicalType.Float, FieldRepetitionType.Required, [floats], Options());

        var doubleBack = ReadHelper.ReadColumn(ReadHelper.Open(doubleBytes), 0, 0, 5).Select(v => BitConverter.DoubleToInt64Bits((double)v!));
        var floatBack = ReadHelper.ReadColumn(ReadHelper.Open(floatBytes), 0, 0, 5).Select(v => BitConverter.SingleToInt32Bits((float)v!));

        await Assert.That(doubleBack).IsEquivalentTo(doubles.Select(BitConverter.DoubleToInt64Bits));
        await Assert.That(floatBack).IsEquivalentTo(floats.Select(BitConverter.SingleToInt32Bits));
        // +0.0 and -0.0 are different values, and so are NaNs with different payloads: 9 distinct doubles, 9 distinct floats.
        await Assert.That(TestParquetFile.Parse(doubleBytes).Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(9);
        await Assert.That(TestParquetFile.Parse(floatBytes).Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(9);
    }

    [Test]
    public async Task IntegerExtremes_RoundTripThroughDictionaries()
    {
        int[] ints = [int.MinValue, -1, 0, 1, int.MaxValue, int.MinValue, int.MaxValue, 0];
        long[] longs = [long.MinValue, -1, 0, 1, long.MaxValue, long.MinValue, long.MaxValue, 0];

        var intBack = ReadHelper.ReadColumn(ReadHelper.Open(WriteColumn(PhysicalType.Int32, FieldRepetitionType.Required, [ints], Options())), 0, 0, 3);
        var longBack = ReadHelper.ReadColumn(ReadHelper.Open(WriteColumn(PhysicalType.Int64, FieldRepetitionType.Required, [longs], Options())), 0, 0, 3);

        await Assert.That(intBack.Select(v => (int)v!)).IsEquivalentTo(ints);
        await Assert.That(longBack.Select(v => (long)v!)).IsEquivalentTo(longs);
    }

    [Test]
    public async Task ByteArrays_StringsAndEmptyValues_RoundTrip()
    {
        string[] strings = ["alpha", "", "δelta", "alpha", "", "a much longer string than the others, repeated", "δelta", "alpha", "a much longer string than the others, repeated"];
        var schema = new ParquetSchema([new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, Options(), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteStrings(strings);
        }

        var file = TestParquetFile.Parse(stream.ToArray());
        file.AssertStructure();

        await Assert.That(file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(4);
        await Assert.That(file.ReadStrings(0, 0).Select(s => s!)).IsEquivalentTo(strings);
        await Assert.That(ReadHelper.ReadColumn(ReadHelper.Open(stream.ToArray()), 0, 0, 2).Select(v => System.Text.Encoding.UTF8.GetString((byte[])v!))).IsEquivalentTo(strings);
    }

    [Test]
    public async Task StringsAndByteArrays_ShareOneDictionaryByContent()
    {
        var schema = new ParquetSchema([new PrimitiveNode("b", PhysicalType.ByteArray, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, Options(), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            var column = group.NextColumn();
            column.WriteStrings(["abc", "xyz"]);
            column.WriteByteArrays([new byte[] { (byte)'a', (byte)'b', (byte)'c' }, new byte[] { 1, 2 }]);
        }

        var file = TestParquetFile.Parse(stream.ToArray());

        await Assert.That(file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(3);
    }

    [Test]
    public async Task FixedLengthByteArrays_RoundTrip_AndEntriesAreComparedWhole()
    {
        // 4-byte entries: the byte sequences overlap when entries are concatenated, but each entry is its own value.
        byte[] values = [1, 2, 3, 4, 2, 3, 4, 1, 1, 2, 3, 4, 0, 0, 0, 0, 2, 3, 4, 1];
        var schema = new ParquetSchema([new PrimitiveNode("f", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, typeLength: 4)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, Options(), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteFixedLengthByteArrays(values);
        }

        var file = TestParquetFile.Parse(stream.ToArray());
        file.AssertStructure();
        var back = ReadHelper.ReadColumn(ReadHelper.Open(stream.ToArray()), 0, 0, 2).SelectMany(v => (byte[])v!);

        await Assert.That(file.Pages(0, 0)[0].Header.DictionaryPageHeader!.NumValues).IsEqualTo(3);
        await Assert.That(back).IsEquivalentTo(values);
    }

    // ---- nulls ----

    [Test]
    public async Task AnAllNullColumn_RoundTrips()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int64, FieldRepetitionType.Optional)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, Options(), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(ReadOnlySpan<long>.Empty, new int[1000]);
        }

        TestParquetFile.Parse(stream.ToArray()).AssertStructure();
        var back = ReadHelper.ReadColumn(ReadHelper.Open(stream.ToArray()), 0, 0, 300);

        await Assert.That(back.Length).IsEqualTo(1000);
        await Assert.That(back.All(v => v is null)).IsTrue();
    }

    [Test]
    public async Task NullsBetweenRepeatedValues_KeepTheirPositions()
    {
        int[] levels = [1, 0, 1, 1, 0, 0, 1, 0, 1, 1];
        int[] values = [5, 6, 5, 6, 5, 5];
        var bytes = WriteColumn(PhysicalType.Int32, FieldRepetitionType.Optional, [values], Options(), [levels]);

        var back = ReadHelper.ReadColumn(ReadHelper.Open(bytes), 0, 0, 4);

        await Assert.That(back.Select(v => (int?)v)).IsEquivalentTo(new int?[] { 5, null, 6, 5, null, null, 6, null, 5, 5 });
    }

    // ---- reading with partial batches ----

    [Test]
    public async Task ReadingDictionaryPagesInOddBatchSizes_GivesTheSameValues()
    {
        var bytes = SampleFile.Build(optional: true, rows: 1200, options: Options(pageSize: 300));
        using var reader = ReadHelper.Open(bytes);

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            foreach (int batch in new[] { 1, 3, 64, 5000 })
            {
                var actual = ReadHelper.ReadColumn(reader, 0, column, batch);
                await Assert.That(SampleFile.FirstDifference(actual, true, 0, 1200, column)).IsNull().Because($"{SampleFile.ColumnNames[column]} batch {batch}");
            }
        }
    }

    [Test]
    public async Task DictionaryByteArrays_StayValidAcrossPageBoundaries()
    {
        // Views into the dictionary outlive the page they came from, but a batch still ends at the page boundary.
        string[] values = Enumerable.Range(0, 600).Select(i => "value-" + i % 5).ToArray();
        var schema = new ParquetSchema([new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, Options(pageSize: 200), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteStrings(values);
        }

        using var reader = ReadHelper.Open(stream.ToArray());
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var batch = new ReadOnlyMemory<byte>[1000];
        var seen = new List<string>();
        BatchRead read;
        while ((read = column.ReadByteArrays(batch)).Rows > 0)
        {
            seen.AddRange(batch.Take(read.Values).Select(b => System.Text.Encoding.UTF8.GetString(b.Span)));
        }

        await Assert.That(seen).IsEquivalentTo(values);
    }
}
