using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Core;
using ParquetNeo.Compression;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class ReaderRobustnessTests
{
    private static byte[] ValidFile() => SampleFile.Build(optional: true, rows: 400, options: new ParquetWriterOptions { DataPageSize = 500 });

    // ---- the file as a whole ----

    [Test]
    public async Task ThingsThatAreNotParquetFiles_AreRejected()
    {
        var random = new byte[200];
        new Random(1).NextBytes(random);

        await Assert.That(() => ReadHelper.Open([])).Throws<ParquetFormatException>();
        await Assert.That(() => ReadHelper.Open("PAR1"u8.ToArray())).Throws<ParquetFormatException>();
        await Assert.That(() => ReadHelper.Open("PAR1PAR1PAR1"u8.ToArray())).Throws<ParquetFormatException>(); // a zero-length footer
        await Assert.That(() => ReadHelper.Open(random)).Throws<ParquetFormatException>();
        await Assert.That(() => ReadHelper.Open("this is a text file, certainly not Parquet at all"u8.ToArray())).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task BadMagicNumbers_AreRejected()
    {
        var atStart = ValidFile();
        atStart[0] = (byte)'X';
        var atEnd = ValidFile();
        atEnd[^1] = (byte)'X';

        await Assert.That(() => ReadHelper.Open(atStart)).Throws<ParquetFormatException>();
        await Assert.That(() => ReadHelper.Open(atEnd)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task EncryptedFiles_AreReportedAsUnsupported()
    {
        var encryptedFooter = ValidFile();
        "PARE"u8.CopyTo(encryptedFooter.AsSpan(encryptedFooter.Length - 4));
        var encryptedStart = ValidFile();
        "PARE"u8.CopyTo(encryptedStart);

        await Assert.That(() => ReadHelper.Open(encryptedFooter)).Throws<NotSupportedException>();
        await Assert.That(() => ReadHelper.Open(encryptedStart)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task ImpossibleFooterLengths_AreRejected()
    {
        foreach (int length in new[] { 0, -1, int.MinValue, int.MaxValue, ValidFile().Length })
        {
            var bytes = ValidFile();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), length);

            await Assert.That(() => ReadHelper.Open(bytes)).Throws<ParquetFormatException>().Because($"footer length {length}");
        }

        var tooBig = new ParquetReaderOptions { Codecs = TestParquetFile.Registry, MaxFooterSize = 50 };
        await Assert.That(() => ReadHelper.Open(ValidFile(), tooBig)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task EveryTruncation_IsRejectedCleanly()
    {
        var bytes = ValidFile();

        for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 120))
        {
            var truncated = bytes[..cut];

            await Assert.That(() => ReadHelper.Open(truncated)).Throws<ParquetFormatException>().Because($"cut at {cut} of {bytes.Length}");
        }
    }

    [Test]
    public async Task AFooterFilledWithGarbage_IsRejectedOrHarmless()
    {
        var bytes = ValidFile();
        var file = TestParquetFile.Parse(bytes);
        var random = new Random(3);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var damaged = (byte[])bytes.Clone();
            int footerLength = bytes.Length - 8 - (int)file.FooterStart;
            for (int k = 0; k < 4; k++)
            {
                damaged[(int)file.FooterStart + random.Next(footerLength)] = (byte)random.Next(256);
            }

            await Assert.That(Probe(damaged)).IsNull().Because($"attempt {attempt}");
        }
    }

    [Test]
    public async Task ANonSeekableOrUnreadableStream_IsRejected()
    {
        await Assert.That(() => new ParquetFileReader(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ParquetFileReader(new WriteOnlyStream())).Throws<ArgumentException>();
        await Assert.That(() => new ParquetFileReader(new NonSeekableStream(ValidFile()))).Throws<ArgumentException>();
    }

    // ---- metadata that contradicts the file ----

    /// <summary>Points the chunk at <paramref name="offset"/>, whether it starts with a dictionary page or a data page.</summary>
    private static void MoveChunk(ColumnMetaData chunk, long offset)
    {
        chunk.DataPageOffset = offset;
        if (chunk.DictionaryPageOffset is not null)
        {
            chunk.DictionaryPageOffset = offset;
        }
    }

    [Test]
    public async Task ColumnChunksOutsideTheFile_AreRejectedWhenTheColumnIsOpened()
    {
        foreach (var edit in new (string Name, Action<ColumnMetaData> Change)[]
        {
            ("page offset past the end", m => MoveChunk(m, 1_000_000_000)),
            ("page offset before the magic", m => MoveChunk(m, 1)),
            ("negative page offset", m => MoveChunk(m, -5)),
            ("size past the end", m => m.TotalCompressedSize = 1_000_000_000),
            ("negative size", m => m.TotalCompressedSize = -1),
            ("negative value count", m => m.NumValues = -1),
        })
        {
            var bytes = Rewrite(ValidFile(), f => edit.Change(f.RowGroups[0].Columns[0].MetaData!));
            using var reader = ReadHelper.Open(bytes);

            await Assert.That(() => reader.OpenRowGroup(0).OpenColumn(0)).Throws<ParquetFormatException>().Because(edit.Name);
        }
    }

    [Test]
    public async Task ValueCountsThatDisagreeWithThePages_AreReported()
    {
        foreach (long wrong in new long[] { 399, 401, 100_000 })
        {
            var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns[0].MetaData!.NumValues = wrong);
            using var reader = ReadHelper.Open(bytes);

            await Assert.That(() => ReadHelper.ReadColumn(reader, 0, 0, 50)).Throws<ParquetFormatException>().Because($"declared {wrong}");
        }
    }

    [Test]
    public async Task APageThatOverrunsItsColumnChunk_IsReported()
    {
        var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns[0].MetaData!.TotalCompressedSize -= 10);
        using var reader = ReadHelper.Open(bytes);

        await Assert.That(() => ReadHelper.ReadColumn(reader, 0, 0, 50)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task ARowGroupWithTheWrongNumberOfColumns_IsRejected()
    {
        var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns.RemoveAt(3));

        await Assert.That(() => ReadHelper.Open(bytes)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task AChunkWhoseTypeContradictsTheSchema_IsRejected()
    {
        var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns[0].MetaData!.Type = PhysicalType.Int64);
        using var reader = ReadHelper.Open(bytes);

        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn(0)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task AColumnStoredInAnotherFile_IsReportedAsUnsupported()
    {
        var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns[0].FilePath = "elsewhere.parquet");
        using var reader = ReadHelper.Open(bytes);

        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn(0)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task ARowGroupWithoutChunkMetadata_IsReportedAsUnsupported()
    {
        var bytes = Rewrite(ValidFile(), f => f.RowGroups[0].Columns[0].MetaData = null);
        using var reader = ReadHelper.Open(bytes);

        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn(0)).Throws<NotSupportedException>();
    }

    // ---- damaged pages ----

    [Test]
    public async Task AGarbagePageHeader_IsReported()
    {
        var bytes = ValidFile();
        var file = TestParquetFile.Parse(bytes);
        long pageStart = TestParquetFile.FirstPageOffset(file.Chunk(0, 0));
        for (int i = 0; i < 12; i++)
        {
            bytes[pageStart + i] = 0xFF;
        }

        using var reader = ReadHelper.Open(bytes);

        await Assert.That(() => ReadHelper.ReadColumn(reader, 0, 0, 50)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task PagesOverTheConfiguredLimits_AreRejected()
    {
        var bytes = SampleFile.Build(optional: true, rows: 2000);

        var smallPages = new ParquetReaderOptions { Codecs = TestParquetFile.Registry, MaxPageSize = 1000 };
        var fewRows = new ParquetReaderOptions { Codecs = TestParquetFile.Registry, MaxRowsPerPage = 100 };

        using var first = ReadHelper.Open(bytes, smallPages);
        using var second = ReadHelper.Open(bytes, fewRows);
        await Assert.That(() => ReadHelper.ReadColumn(first, 0, 1, 100)).Throws<ParquetFormatException>();
        await Assert.That(() => ReadHelper.ReadColumn(second, 0, 0, 100)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task DamagedCompressedPages_AreReportedForEveryCodec()
    {
        foreach (var codec in new[] { CompressionCodec.Gzip, CompressionCodec.Brotli, CompressionCodec.Snappy, CompressionCodec.Zstd, CompressionCodec.Lz4Raw })
        {
            var options = new ParquetWriterOptions { Compression = codec, Codecs = TestParquetFile.Registry };
            var bytes = SampleFile.Build(optional: false, rows: 500, options: options);
            var file = TestParquetFile.Parse(bytes);
            var page = file.Pages(0, 5)[0];
            long body = page.Offset + page.HeaderLength;
            for (int i = 0; i < Math.Min(40, page.Header.CompressedPageSize); i++)
            {
                bytes[body + i] = 0xAA;
            }

            using var reader = ReadHelper.Open(bytes);

            var exception = Record(() => ReadHelper.ReadColumn(reader, 0, 5, 100));
            await Assert.That(exception is null or ParquetFormatException).IsTrue().Because($"{codec}: {exception}");
        }
    }

    // ---- damaged dictionaries ----

    /// <summary>A required INT32 column of 300 values with 3 distinct ones: one dictionary page, one dictionary-encoded data page.</summary>
    private static byte[] ThreeValueDictionaryFile(CompressionCodec codec = CompressionCodec.Uncompressed)
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int32, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { Compression = codec, Codecs = TestParquetFile.Registry }, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(Enumerable.Range(0, 300).Select(i => i % 3).ToArray());
        }

        return stream.ToArray();
    }

    private static byte[] PatchPageHeader(byte[] original, int pageIndex, Action<PageHeader> change) =>
        FooterEditor.PatchPageHeader(original, pageIndex, change);

    private static Exception? ReadAll(byte[] bytes)
    {
        try
        {
            using var reader = ReadHelper.Open(bytes);
            ReadHelper.ReadColumn(reader, 0, 0, 100);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Test]
    public async Task TheUndamagedDictionaryFile_Reads()
    {
        await Assert.That(ReadAll(ThreeValueDictionaryFile())).IsNull();
        await Assert.That(ReadAll(ThreeValueDictionaryFile(CompressionCodec.Gzip))).IsNull();
    }

    [Test]
    public async Task IndexesPastTheEndOfTheDictionary_AreReported()
    {
        // Claim the dictionary has 2 entries while the pages index 3.
        var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 0, h => h.DictionaryPageHeader!.NumValues = 2);

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("dictionary");
    }

    [Test]
    public async Task ADictionaryLargerThanItsPage_IsReported()
    {
        var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 0, h => h.DictionaryPageHeader!.NumValues = 9);

        await Assert.That(ReadAll(bytes)).IsTypeOf<ParquetFormatException>();
    }

    [Test]
    public async Task ANegativeOrHugeDictionaryCount_IsReported()
    {
        foreach (int count in new[] { -1, 63, 100_000_000, int.MaxValue })
        {
            var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 0, h => h.DictionaryPageHeader!.NumValues = count);

            await Assert.That(ReadAll(bytes)).IsTypeOf<ParquetFormatException>().Because($"count {count}");
        }
    }

    [Test]
    public async Task ADictionaryCountThatTheBytesCannotHold_IsRejectedBeforeAnythingIsAllocated()
    {
        var strings = StringDictionaryFile();
        foreach (var original in new[] { ThreeValueDictionaryFile(), strings })
        {
            var bytes = PatchPageHeader(original, 0, h => h.DictionaryPageHeader!.NumValues = 10_000_000);
            _ = ReadAll(bytes); // warm up, so one-time initialization is not counted

            long before = GC.GetAllocatedBytesForCurrentThread();
            var exception = ReadAll(bytes);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // "does not fit" comes from the size check; without it the failure would only surface after allocating the lot.
            await Assert.That(exception).IsTypeOf<ParquetFormatException>();
            await Assert.That(exception!.Message).Contains("does not fit");
            await Assert.That(allocated).IsLessThan(2_000_000);
        }
    }

    /// <summary>A required BYTE_ARRAY column of 300 values with 3 distinct strings.</summary>
    private static byte[] StringDictionaryFile()
    {
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteStrings(Enumerable.Range(0, 300).Select(i => "value" + i % 3).ToArray());
        }

        return stream.ToArray();
    }

    [Test]
    public async Task ADictionaryPageAfterADataPage_IsReported()
    {
        // A plain file whose second page claims to be a dictionary page.
        var schema = new ParquetSchema([new PrimitiveNode("c", PhysicalType.Int32, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { DictionaryEnabled = false, DataPageSize = 400 }, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(Enumerable.Range(0, 300).ToArray());
        }

        var bytes = PatchPageHeader(stream.ToArray(), 1, h =>
        {
            int count = h.DataPageHeader!.NumValues;
            h.Type = PageType.DictionaryPage;
            h.DataPageHeader = null;
            h.DictionaryPageHeader = new DictionaryPageHeader { NumValues = count, Encoding = ParquetEncoding.Plain };
        });

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("after a data page");
    }

    [Test]
    public async Task APageThatUsesADictionaryWithoutOne_IsReported()
    {
        // Turn the dictionary page into an index page, which readers skip.
        var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 0, h =>
        {
            h.Type = PageType.IndexPage;
        });

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("no dictionary page");
    }

    [Test]
    public async Task ADictionaryEncodingOtherThanPlain_IsReportedAsUnsupported()
    {
        var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 0, h => h.DictionaryPageHeader!.Encoding = ParquetEncoding.DeltaBinaryPacked);

        await Assert.That(ReadAll(bytes)).IsTypeOf<NotSupportedException>();
    }

    [Test]
    public async Task ADictionaryEncodedPageWithoutADictionaryPage_IsReported()
    {
        // The dictionary page's bytes are cut out of the file and the metadata updated to match.
        var original = ThreeValueDictionaryFile();
        var file = TestParquetFile.Parse(original);
        var pages = file.Pages(0, 0);
        int dictionaryBytes = pages[0].HeaderLength + pages[0].Header.CompressedPageSize;
        var withoutDictionary = original.AsSpan(0, (int)pages[0].Offset).ToArray()
            .Concat(original.AsSpan((int)pages[1].Offset, (int)file.FooterStart - (int)pages[1].Offset).ToArray()).ToArray();
        var rebuilt = Rewrite(withoutDictionary.Concat(original[(int)file.FooterStart..]).ToArray(), f =>
        {
            var chunk = f.RowGroups[0].Columns[0].MetaData!;
            chunk.DictionaryPageOffset = null;
            chunk.DataPageOffset = pages[0].Offset;
            chunk.TotalCompressedSize -= dictionaryBytes;
            chunk.TotalUncompressedSize -= dictionaryBytes;
            f.RowGroups[0].TotalByteSize -= dictionaryBytes;
            f.RowGroups[0].TotalCompressedSize -= dictionaryBytes;
        }, withoutDictionary.Length);

        var exception = ReadAll(rebuilt);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("no dictionary page");
    }

    [Test]
    public async Task ASecondDictionaryPage_IsReported()
    {
        var original = ThreeValueDictionaryFile();
        var pages = TestParquetFile.Parse(original).Pages(0, 0);
        int dictionaryLength = pages[0].HeaderLength + pages[0].Header.CompressedPageSize;
        int footerStart = (int)TestParquetFile.Parse(original).FooterStart;

        // Insert a copy of the dictionary page right after the original; the chunk grows by one page.
        var body = new List<byte>(original.AsSpan(0, footerStart).ToArray());
        body.InsertRange((int)pages[0].Offset + dictionaryLength, original.AsSpan((int)pages[0].Offset, dictionaryLength).ToArray());
        var spliced = Rewrite(body.Concat(original[footerStart..]).ToArray(), f =>
        {
            var chunk = f.RowGroups[0].Columns[0].MetaData!;
            chunk.DataPageOffset += dictionaryLength;
            chunk.TotalCompressedSize += dictionaryLength;
            chunk.TotalUncompressedSize += dictionaryLength;
        }, footerStart + dictionaryLength);

        var exception = ReadAll(spliced);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("more than one dictionary page");
    }

    [Test]
    public async Task ABitWidthAbove32_IsReported()
    {
        var bytes = ThreeValueDictionaryFile();
        var page = TestParquetFile.Parse(bytes).Pages(0, 0)[1];
        bytes[page.Offset + page.HeaderLength] = 33;

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("bit width");
    }

    [Test]
    public async Task ADictionaryEncodedBooleanColumn_IsReported()
    {
        var schema = new ParquetSchema([new PrimitiveNode("b", PhysicalType.Boolean, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(new bool[40]);
        }

        var bytes = PatchPageHeader(stream.ToArray(), 0, h => h.DataPageHeader!.Encoding = ParquetEncoding.RleDictionary);

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
        await Assert.That(exception!.Message).Contains("boolean");
    }

    [Test]
    public async Task MoreValuesThanIndexes_AreReported()
    {
        // The page header promises 400 values; its index data only holds 300 (rounded up to 304 by bit packing).
        var bytes = PatchPageHeader(ThreeValueDictionaryFile(), 1, h => h.DataPageHeader!.NumValues = 400);

        var exception = ReadAll(bytes);

        await Assert.That(exception).IsTypeOf<ParquetFormatException>();
    }

    // ---- fuzzing ----

    [Test]
    public async Task RandomlyDamagedFiles_OnlyEverFailWithParquetExceptions()
    {
        var files = new List<(string Name, byte[] Bytes)>
        {
            ("uncompressed", ValidFile()),
            ("gzip", SampleFile.Build(true, 300, options: new ParquetWriterOptions { Compression = CompressionCodec.Gzip, DataPageSize = 700 })),
            ("snappy", SampleFile.Build(false, 300, 2, new ParquetWriterOptions { Compression = CompressionCodec.Snappy, Codecs = TestParquetFile.Registry })),
            ("pyarrow v2", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-v2.parquet"))),
            ("pyarrow v1 snappy", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-v1-smallpages.parquet"))),
            ("pyarrow dictionary v1 snappy", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-dict-v1-snappy-smallpages.parquet"))),
            ("pyarrow dictionary legacy", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-dict-legacy.parquet"))),
            ("pyarrow dictionary fallback", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-required-dict-fallback.parquet"))),
            ("our dictionary file", ThreeValueDictionaryFile()),
            ("pyarrow nested v1", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-v1-smallpages.parquet"))),
            ("pyarrow nested v2 snappy", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-v2-snappy.parquet"))),
            ("pyarrow nested dictionary", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-dictionary-v1.parquet"))),
            ("our nested file", File.ReadAllBytes(TestBytes.FixturePath("golden-nested-dictionary.parquet"))),
            ("pyarrow encodings v1", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-encodings-optional-v1.parquet"))),
            ("pyarrow encodings v2", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-encodings-optional-v2-smallpages.parquet"))),
            ("our encodings", File.ReadAllBytes(TestBytes.FixturePath("golden-encodings.parquet"))),
            ("pyarrow statistics", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-statistics-plain.parquet"))),
            ("pyarrow sample statistics", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-flat-optional-statistics.parquet"))),
        };

        foreach (var (name, original) in files)
        {
            var random = new Random(name.GetHashCode());
            for (int attempt = 0; attempt < 400; attempt++)
            {
                var damaged = (byte[])original.Clone();
                for (int k = random.Next(1, 4); k > 0; k--)
                {
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                }

                await Assert.That(Probe(damaged)).IsNull().Because($"{name}, attempt {attempt}");
            }
        }
    }

    // ---- misuse ----

    [Test]
    public async Task ABatchOfTheWrongType_IsRejected()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        using var column = reader.OpenRowGroup(0).OpenColumn(0); // INT32

        await Assert.That(() => column.ReadBatch(new long[10])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadBatch(new double[10])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadBatch(new bool[10])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadByteArrays(new ReadOnlyMemory<byte>[10])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadFixedLengthByteArrays(new byte[40])).Throws<ArgumentException>();
    }

    [Test]
    public async Task LevelsAndValues_MustMatchTheColumnKind()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 10));
        using var optional = reader.OpenRowGroup(0).OpenColumn(0);

        await Assert.That(() => optional.ReadBatch(new int[10])).Throws<ArgumentException>(); // no span for the levels
        await Assert.That(() => optional.ReadBatch(new int[5], new int[10])).Throws<ArgumentException>(); // not enough room for the values

        using var requiredReader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        using var required = requiredReader.OpenRowGroup(0).OpenColumn(0);
        await Assert.That(() => required.ReadBatch(new int[10], new int[10])).Throws<ArgumentException>();
    }

    [Test]
    public async Task FixedLengthDestinations_MustBeWholeEntries()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        using var column = reader.OpenRowGroup(0).OpenColumn("fixed");

        await Assert.That(() => column.ReadFixedLengthByteArrays(new byte[10])).Throws<ArgumentException>();
    }

    [Test]
    public async Task UsingDisposedReaders_IsRejected()
    {
        var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        var group = reader.OpenRowGroup(0);
        var column = group.OpenColumn(0);
        column.Dispose();
        column.Dispose(); // harmless
        await Assert.That(() => column.ReadBatch(new int[10])).Throws<ObjectDisposedException>();

        var other = group.OpenColumn(1);
        reader.Dispose();
        reader.Dispose(); // harmless
        await Assert.That(() => other.ReadBatch(new long[10])).Throws<ObjectDisposedException>();
        await Assert.That(() => reader.OpenRowGroup(0)).Throws<ObjectDisposedException>();
        await Assert.That(() => group.OpenColumn(2)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task LeaveOpen_DecidesWhetherTheStreamIsClosed()
    {
        var bytes = ValidFile();

        var kept = new TrackingStream(bytes);
        new ParquetFileReader(kept, new ParquetReaderOptions { Codecs = TestParquetFile.Registry }, leaveOpen: true).Dispose();
        var closed = new TrackingStream(bytes);
        new ParquetFileReader(closed, new ParquetReaderOptions { Codecs = TestParquetFile.Registry }).Dispose();
        var failed = new TrackingStream([1, 2, 3]);
        Assert.Throws<ParquetFormatException>(() => new ParquetFileReader(failed));

        await Assert.That(kept.IsDisposed).IsFalse();
        await Assert.That(closed.IsDisposed).IsTrue();
        await Assert.That(failed.IsDisposed).IsTrue(); // a constructor that fails does not leak the stream
    }

    [Test]
    public async Task DisposeAsync_ReleasesTheStream()
    {
        var stream = new TrackingStream(ValidFile());

        await using (new ParquetFileReader(stream, new ParquetReaderOptions { Codecs = TestParquetFile.Registry }))
        {
        }

        await Assert.That(stream.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ColumnsOfOneFile_CanBeReadInterleaved()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 3000, options: new ParquetWriterOptions { DataPageSize = 400 }));
        using var a = reader.OpenRowGroup(0).OpenColumn(1);
        using var b = reader.OpenRowGroup(0).OpenColumn(3);
        var valuesA = new long[7];
        var valuesB = new double[11];
        var levelsA = new int[7];
        var levelsB = new int[11];
        long rowsA = 0, rowsB = 0;

        // They share one stream, and each read seeks to its own position first.
        while (true)
        {
            var readA = a.ReadBatch(valuesA, levelsA);
            var readB = b.ReadBatch(valuesB, levelsB);
            rowsA += readA.Entries;
            rowsB += readB.Entries;
            if (readA.Entries == 0 && readB.Entries == 0)
            {
                break;
            }
        }

        await Assert.That(rowsA).IsEqualTo(3000L);
        await Assert.That(rowsB).IsEqualTo(3000L);
    }

    // ---- helpers ----

    /// <summary>Opens the file and reads every column fully; returns null unless something other than a Parquet exception escaped.</summary>
    private static string? Probe(byte[] bytes)
    {
        try
        {
            var options = new ParquetReaderOptions { Codecs = TestParquetFile.Registry, MaxPageSize = 1 << 20, MaxRowsPerPage = 1 << 18, MaxFooterSize = 1 << 20 };
            using var reader = new ParquetFileReader(new MemoryStream(bytes), options);
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                for (int c = 0; c < reader.Schema.Columns.Count; c++)
                {
                    try
                    {
                        reader.OpenRowGroup(g).GetStatistics(c);
                        ReadHelper.ReadEntries(reader, g, c, 100);
                    }
                    catch (NotSupportedException)
                    {
                        // e.g. a damaged encoding id: reported, not a crash
                    }
                }
            }

            return null;
        }
        catch (ParquetFormatException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>Replaces the footer of a file with a modified copy of its metadata.</summary>
    private static byte[] Rewrite(byte[] bytes, Action<FileMetaData> change, int? footerStart = null) =>
        FooterEditor.Rewrite(bytes, change, footerStart);

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
