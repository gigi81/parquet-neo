using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Core;

/// <summary>Pages carry a CRC-32 when asked, and a reader that is asked to checks it.</summary>
public class PageChecksumTests
{
    private static readonly ParquetReaderOptions Verify = new() { VerifyPageChecksums = true };

    private static byte[] Write(ParquetWriterOptions options, int rows = 2000)
    {
        var stream = new MemoryStream();
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("Name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        ]);
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(Enumerable.Range(0, rows).Select(i => (long)i * 31).ToArray());
            var names = Enumerable.Range(0, rows).Where(i => i % 5 != 0).Select(i => $"name-{i % 40}").ToArray();
            group.NextColumn().WriteStrings(names, Enumerable.Range(0, rows).Select(i => i % 5 != 0 ? 1 : 0).ToArray());
        }

        return stream.ToArray();
    }

    private static (long[] Ids, string[] Names) Read(byte[] file, ParquetReaderOptions? options)
    {
        using var reader = new ParquetFileReader(new MemoryStream(file), options);
        var group = reader.OpenRowGroup(0);
        var ids = new long[(int)group.RowCount];
        using (var column = group.OpenColumn(0))
        {
            int read = 0;
            while (read < ids.Length)
            {
                int batch = column.ReadBatch(ids.AsSpan(read)).Values;
                if (batch == 0)
                {
                    break;
                }

                read += batch;
            }
        }

        var names = new string[(int)group.RowCount];
        using (var column = group.OpenColumn(1))
        {
            var levels = new int[names.Length];
            int read = 0;
            while (read < names.Length)
            {
                int entries = column.ReadStrings(names.AsSpan(read), levels.AsSpan(read)).Entries;
                if (entries == 0)
                {
                    break;
                }

                read += entries;
            }
        }

        return (ids, names);
    }

    /// <summary>The page headers of a column chunk, in order, and where each page body starts.</summary>
    private static List<(PageHeader Header, long BodyStart)> Pages(byte[] file, int column)
    {
        using var reader = new ParquetFileReader(new MemoryStream(file));
        var meta = reader.Metadata.RowGroups[0].Columns[column].MetaData!;
        long start = meta.DictionaryPageOffset is > 0 ? meta.DictionaryPageOffset.Value : meta.DataPageOffset;
        long end = start + meta.TotalCompressedSize;
        var pages = new List<(PageHeader, long)>();
        for (long position = start; position < end;)
        {
            var header = PageHeader.Deserialize(file.AsSpan((int)position, (int)Math.Min(end - position, 200)), out int length);
            pages.Add((header, position + length));
            position += length + header.CompressedPageSize;
        }

        return pages;
    }

    public static IEnumerable<(DataPageVersion Version, CompressionCodec Codec, bool Dictionary)> Settings() =>
        from version in new[] { DataPageVersion.V1, DataPageVersion.V2 }
        from codec in new[] { CompressionCodec.Uncompressed, CompressionCodec.Gzip }
        from dictionary in new[] { true, false }
        select (version, codec, dictionary);

    private static ParquetWriterOptions Options(DataPageVersion version, CompressionCodec codec, bool dictionary, bool checksums) =>
        new() { DataPageVersion = version, Compression = codec, DictionaryEnabled = dictionary, WritePageChecksums = checksums, DataPageSize = 2000 };

    [Test]
    [MethodDataSource(nameof(Settings))]
    public async Task EveryPage_HasTheChecksumOfItsBody(DataPageVersion version, CompressionCodec codec, bool dictionary)
    {
        byte[] file = Write(Options(version, codec, dictionary, checksums: true));

        foreach (int column in new[] { 0, 1 })
        {
            var pages = Pages(file, column);
            await Assert.That(pages.Count).IsGreaterThan(1);
            foreach (var (header, bodyStart) in pages)
            {
                uint expected = Crc32.Compute(file.AsSpan((int)bodyStart, header.CompressedPageSize));
                await Assert.That(header.Crc).IsEqualTo((int)expected);
            }
        }
    }

    [Test]
    public async Task NoPage_HasAChecksum_UnlessAskedFor()
    {
        byte[] file = Write(Options(DataPageVersion.V1, CompressionCodec.Uncompressed, dictionary: true, checksums: false));

        await Assert.That(Pages(file, 0).All(page => page.Header.Crc is null)).IsTrue();
        await Assert.That(Pages(file, 1).All(page => page.Header.Crc is null)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Settings))]
    public async Task TheValuesAreTheSame_WithChecksumsAndWithout_AndTheyVerify(DataPageVersion version, CompressionCodec codec, bool dictionary)
    {
        byte[] plain = Write(Options(version, codec, dictionary, checksums: false));
        byte[] summed = Write(Options(version, codec, dictionary, checksums: true));

        var expected = Read(plain, null);
        var verified = Read(summed, Verify);
        var unverified = Read(summed, null);

        await Assert.That(verified.Ids).IsEquivalentTo(expected.Ids);
        await Assert.That(verified.Names).IsEquivalentTo(expected.Names);
        await Assert.That(unverified.Ids).IsEquivalentTo(expected.Ids);
        await Assert.That(Read(plain, Verify).Ids).IsEquivalentTo(expected.Ids);
    }

    [Test]
    [MethodDataSource(nameof(Settings))]
    public async Task ADamagedBody_IsFoundWhenVerified_InEveryKindOfPage(DataPageVersion version, CompressionCodec codec, bool dictionary)
    {
        byte[] file = Write(Options(version, codec, dictionary, checksums: true));
        foreach (int column in new[] { 0, 1 })
        {
            foreach (var (header, bodyStart) in Pages(file, column))
            {
                // A byte in the middle of the body, flipped.
                var damaged = (byte[])file.Clone();
                damaged[(int)bodyStart + (header.CompressedPageSize / 2)] ^= 0x5A;

                await Assert.That(() => Read(damaged, Verify)).Throws<CorruptColumnException>();
            }
        }
    }

    [Test]
    public async Task ADamagedBody_IsNotFound_WhenNobodyAsked_AndThePageHasNoChecksumToFindItWith()
    {
        byte[] file = Write(Options(DataPageVersion.V1, CompressionCodec.Uncompressed, dictionary: false, checksums: true));
        var (header, bodyStart) = Pages(file, 0)[0];
        var damaged = (byte[])file.Clone();
        damaged[(int)bodyStart + 100] ^= 0x01;

        // Not asked: the damaged number is read as it is.
        var read = Read(damaged, null);
        await Assert.That(read.Ids.SequenceEqual(Read(file, null).Ids)).IsFalse();

        // Asked, but the page has no checksum: nothing to check it against.
        byte[] noChecksum = Write(Options(DataPageVersion.V1, CompressionCodec.Uncompressed, dictionary: false, checksums: false));
        var (_, noChecksumBody) = Pages(noChecksum, 0)[0];
        noChecksum[(int)noChecksumBody + 100] ^= 0x01;
        await Assert.That(Read(noChecksum, Verify).Ids.Length).IsEqualTo(2000);
        await Assert.That(header.Crc).IsNotNull();
    }

    [Test]
    public async Task TheMessage_NamesTheColumnAndBothChecksums()
    {
        byte[] file = Write(Options(DataPageVersion.V1, CompressionCodec.Uncompressed, dictionary: false, checksums: true));
        var (header, bodyStart) = Pages(file, 1)[0];
        file[(int)bodyStart + 3] ^= 0x01;

        var exception = Assert.Throws<CorruptColumnException>(() => Read(file, Verify));

        await Assert.That(exception.ColumnName).IsEqualTo("Name");
        await Assert.That(exception.Message).Contains("checksum");
        await Assert.That(exception.Message).Contains(((uint)header.Crc!.Value).ToString("X8"));
    }
}
