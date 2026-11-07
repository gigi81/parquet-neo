using System.Buffers.Binary;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Lz4;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

/// <summary>
/// A deliberately simple, test-only reader for flat files written by ParquetFileWriter: it walks the footer and
/// the data page v1 of every column chunk and decodes them with the library's PLAIN, RLE and codec building blocks.
/// It also checks the structural invariants of the layout.
/// </summary>
internal sealed class TestParquetFile
{
    public static readonly CodecRegistry Registry = new CodecRegistry().AddSnappy().AddZstd().AddLz4Raw();

    private TestParquetFile(byte[] bytes)
    {
        Bytes = bytes;
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("PAR1"u8) || !bytes.AsSpan(bytes.Length - 4).SequenceEqual("PAR1"u8))
        {
            throw new InvalidDataException("Missing PAR1 magic at the start or end of the file.");
        }

        int footerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        FooterStart = bytes.Length - 8 - footerLength;
        Metadata = FileMetaData.Deserialize(bytes.AsSpan((int)FooterStart, footerLength));
        Schema = ParquetSchema.FromElements(Metadata.Schema);
    }

    public byte[] Bytes { get; }

    public long FooterStart { get; }

    public FileMetaData Metadata { get; }

    public ParquetSchema Schema { get; }

    public static TestParquetFile Parse(byte[] bytes) => new(bytes);

    public record PageInfo(PageHeader Header, long Offset, int HeaderLength);

    public ColumnMetaData Chunk(int rowGroup, int column) => Metadata.RowGroups[rowGroup].Columns[column].MetaData!;

    /// <summary>A chunk starts with its dictionary page when it has one.</summary>
    public static long FirstPageOffset(ColumnMetaData chunk) => chunk.DictionaryPageOffset ?? chunk.DataPageOffset;

    /// <summary>The pages of a column chunk, found by walking the headers from the chunk's first page.</summary>
    public List<PageInfo> Pages(int rowGroup, int column)
    {
        var chunk = Chunk(rowGroup, column);
        var pages = new List<PageInfo>();
        long position = FirstPageOffset(chunk);
        long end = position + chunk.TotalCompressedSize;
        while (position < end)
        {
            var header = PageHeader.Deserialize(Bytes.AsSpan((int)position), out int headerLength);
            pages.Add(new PageInfo(header, position, headerLength));
            position += headerLength + header.CompressedPageSize;
        }

        if (position != end)
        {
            throw new InvalidDataException($"The pages of column {column} overrun its declared size.");
        }

        return pages;
    }

    /// <summary>The data pages of a column chunk (all pages except the dictionary page).</summary>
    public List<PageInfo> DataPages(int rowGroup, int column) =>
        Pages(rowGroup, column).Where(p => p.Header.Type != PageType.DictionaryPage).ToList();

    /// <summary>Decodes a whole column chunk; null entries are nulls. Values are int, long, float, double, bool or byte[].</summary>
    public object?[] ReadColumn(int rowGroup, int column)
    {
        var node = Schema.Columns[column];
        var codec = Registry.Get(Chunk(rowGroup, column).Codec);
        var result = new List<object?>();
        object[]? dictionary = null;

        foreach (var page in Pages(rowGroup, column))
        {
            var header = page.Header;
            var compressed = Bytes.AsSpan((int)page.Offset + page.HeaderLength, header.CompressedPageSize);
            var data = new byte[header.UncompressedPageSize];
            codec.Decompress(compressed, data);

            if (header.Type == PageType.DictionaryPage)
            {
                dictionary = DecodeValues(node, data, header.DictionaryPageHeader!.NumValues);
                continue;
            }

            int rows = header.DataPageHeader!.NumValues;
            int position = 0;
            var levels = Enumerable.Repeat(node.MaxDefinitionLevel, rows).ToArray(); // a required column has no level data: every row is present
            if (node.MaxDefinitionLevel > 0)
            {
                int levelBytes = BinaryPrimitives.ReadInt32LittleEndian(data);
                int width = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)node.MaxDefinitionLevel);
                var decoded = new RleBitPackedDecoder(data.AsSpan(4, levelBytes), width);
                if (decoded.ReadValues(levels) != rows)
                {
                    throw new InvalidDataException("Fewer definition levels than the page declares.");
                }

                position = 4 + levelBytes;
            }

            int present = levels.Count(l => l == node.MaxDefinitionLevel);
            var values = header.DataPageHeader.Encoding == ParquetEncoding.RleDictionary
                ? DecodeIndexed(dictionary ?? throw new InvalidDataException("A dictionary-encoded page without a dictionary page."), data.AsSpan(position), present)
                : DecodeValues(node, data.AsSpan(position), present);
            int next = 0;
            foreach (int level in levels)
            {
                result.Add(level == node.MaxDefinitionLevel ? values[next++] : null);
            }
        }

        return result.ToArray();
    }

    public T?[] ReadColumn<T>(int rowGroup, int column) where T : struct =>
        ReadColumn(rowGroup, column).Select(v => (T?)v).ToArray();

    public string?[] ReadStrings(int rowGroup, int column) =>
        ReadColumn(rowGroup, column).Select(v => v is null ? null : System.Text.Encoding.UTF8.GetString((byte[])v)).ToArray();

    public byte[]?[] ReadBinary(int rowGroup, int column) => ReadColumn(rowGroup, column).Select(v => (byte[]?)v).ToArray();

    /// <summary>For files from other writers: only checks that each column's pages tile its chunk.</summary>
    public void AssertStructureLoosely()
    {
        for (int g = 0; g < Metadata.RowGroups.Count; g++)
        {
            for (int c = 0; c < Schema.Columns.Count; c++)
            {
                Pages(g, c); // throws if the pages do not add up to the declared chunk size
            }
        }
    }

    /// <summary>Asserts everything the layout promises: contiguous chunks, pages that tile their chunk, consistent totals.</summary>
    public void AssertStructure()
    {
        long position = 4; // after the leading magic
        long rows = 0;
        for (int g = 0; g < Metadata.RowGroups.Count; g++)
        {
            var group = Metadata.RowGroups[g];
            long uncompressed = 0, compressed = 0;
            Require(group.FileOffset == position, $"row group {g} starts at {group.FileOffset}, expected {position}");
            Require(group.Ordinal == g, $"row group {g} has ordinal {group.Ordinal}");
            Require(group.Columns.Count == Schema.Columns.Count, $"row group {g} has {group.Columns.Count} chunks");

            for (int c = 0; c < group.Columns.Count; c++)
            {
                var chunk = group.Columns[c].MetaData!;
                Require(group.Columns[c].FileOffset == 0, "ColumnChunk.file_offset is deprecated and must be 0");
                Require(FirstPageOffset(chunk) == position, $"chunk {g}/{c} starts at {FirstPageOffset(chunk)}, expected {position}");
                Require(chunk.PathInSchema.SequenceEqual(Schema.Columns[c].Path), $"chunk {g}/{c} path");
                Require(chunk.Type == Schema.Columns[c].Type, $"chunk {g}/{c} type");

                var pages = Pages(g, c);
                Require(pages.Count > 0, $"chunk {g}/{c} has no pages");
                bool hasDictionary = chunk.DictionaryPageOffset is not null;
                Require(hasDictionary == (pages[0].Header.Type == PageType.DictionaryPage), $"chunk {g}/{c}: the dictionary page is first exactly when dictionary_page_offset is set");
                if (hasDictionary)
                {
                    Require(chunk.DictionaryPageOffset == position, $"chunk {g}/{c}: dictionary page offset");
                    Require(chunk.DataPageOffset == position + pages[0].HeaderLength + pages[0].Header.CompressedPageSize, $"chunk {g}/{c}: data_page_offset is the first data page");
                    Require(pages[0].Header.DictionaryPageHeader!.Encoding == ParquetEncoding.Plain, "the dictionary is PLAIN");
                }
                else
                {
                    Require(chunk.DataPageOffset == position, $"chunk {g}/{c}: data_page_offset");
                }

                long values = 0, chunkUncompressed = 0, chunkCompressed = 0;
                int dictionaryPages = 0, dictionaryDataPages = 0, plainDataPages = 0;
                bool seenPlain = false;
                foreach (var page in pages)
                {
                    if (page.Header.Type == PageType.DictionaryPage)
                    {
                        dictionaryPages++;
                    }
                    else
                    {
                        Require(page.Header.Type == PageType.DataPage, "only data page v1 is written");
                        var encoding = page.Header.DataPageHeader!.Encoding;
                        Require(encoding is ParquetEncoding.Plain or ParquetEncoding.RleDictionary, $"unexpected value encoding {encoding}");
                        if (encoding == ParquetEncoding.Plain)
                        {
                            plainDataPages++;
                            seenPlain = true;
                        }
                        else
                        {
                            Require(hasDictionary && !seenPlain, "dictionary-encoded pages come first, and only in a chunk with a dictionary");
                            dictionaryDataPages++;
                        }

                        values += page.Header.DataPageHeader.NumValues;
                    }

                    chunkUncompressed += page.HeaderLength + page.Header.UncompressedPageSize;
                    chunkCompressed += page.HeaderLength + page.Header.CompressedPageSize;
                }

                Require(dictionaryPages == (hasDictionary ? 1 : 0), "at most one dictionary page");
                Require(chunk.EncodingStats is not null, "encoding_stats are written");
                Require(StatsCount(chunk, PageType.DictionaryPage, ParquetEncoding.Plain) == dictionaryPages, "encoding_stats for the dictionary page");
                Require(StatsCount(chunk, PageType.DataPage, ParquetEncoding.RleDictionary) == dictionaryDataPages, "encoding_stats for dictionary-encoded pages");
                Require(StatsCount(chunk, PageType.DataPage, ParquetEncoding.Plain) == plainDataPages, "encoding_stats for plain pages");
                Require(chunk.Encodings.Contains(ParquetEncoding.RleDictionary) == dictionaryDataPages > 0, "encodings lists RLE_DICTIONARY exactly when it is used");
                Require(chunk.Encodings.Contains(ParquetEncoding.Plain), "PLAIN is always listed (the dictionary page or plain pages use it)");

                Require(values == chunk.NumValues && values == group.NumRows, $"chunk {g}/{c} holds {values} values, expected {group.NumRows}");
                Require(chunkUncompressed == chunk.TotalUncompressedSize, $"chunk {g}/{c} uncompressed size");
                Require(chunkCompressed == chunk.TotalCompressedSize, $"chunk {g}/{c} compressed size");
                uncompressed += chunkUncompressed;
                compressed += chunkCompressed;
                position += chunkCompressed;
            }

            Require(group.TotalByteSize == uncompressed, $"row group {g} total_byte_size");
            Require(group.TotalCompressedSize == compressed, $"row group {g} total_compressed_size");
            rows += group.NumRows;
        }

        Require(position == FooterStart, $"column data ends at {position} but the footer starts at {FooterStart}");
        Require(Metadata.NumRows == rows, "file num_rows");
        Require(Metadata.ColumnOrders!.Count == Schema.Columns.Count, "one column order per column");
    }

    private static int StatsCount(ColumnMetaData chunk, PageType type, ParquetEncoding encoding) =>
        chunk.EncodingStats!.Where(s => s.PageType == type && s.Encoding == encoding).Sum(s => s.Count);

    /// <summary>Decodes a dictionary-encoded page body: a bit-width byte, then RLE/bit-packed indexes.</summary>
    private static object[] DecodeIndexed(object[] dictionary, ReadOnlySpan<byte> data, int count)
    {
        var indexes = new int[count];
        if (count > 0 && new RleBitPackedDecoder(data[1..], data[0]).ReadValues(indexes) != count)
        {
            throw new InvalidDataException("Fewer dictionary indexes than values.");
        }

        return indexes.Select(i => dictionary[i]).ToArray();
    }

    private static object[] DecodeValues(PrimitiveNode node, ReadOnlySpan<byte> data, int count)
    {
        switch (node.Type)
        {
            case PhysicalType.Boolean:
                var bools = new bool[count];
                PlainDecoder.ReadBooleans(data, bools);
                return bools.Select(v => (object)v).ToArray();
            case PhysicalType.Int32:
                var ints = new int[count];
                PlainDecoder.ReadInt32s(data, ints);
                return ints.Select(v => (object)v).ToArray();
            case PhysicalType.Int64:
                var longs = new long[count];
                PlainDecoder.ReadInt64s(data, longs);
                return longs.Select(v => (object)v).ToArray();
            case PhysicalType.Float:
                var floats = new float[count];
                PlainDecoder.ReadSingles(data, floats);
                return floats.Select(v => (object)v).ToArray();
            case PhysicalType.Double:
                var doubles = new double[count];
                PlainDecoder.ReadDoubles(data, doubles);
                return doubles.Select(v => (object)v).ToArray();
            case PhysicalType.ByteArray:
                var ranges = new ByteRange[count];
                PlainDecoder.ReadByteArrays(data, ranges);
                var arrays = new object[count];
                for (int i = 0; i < count; i++)
                {
                    arrays[i] = data.Slice(ranges[i].Offset, ranges[i].Length).ToArray();
                }

                return arrays;
            case PhysicalType.FixedLenByteArray:
                int length = node.TypeLength!.Value;
                var fixedBytes = PlainDecoder.ReadFixedLengthByteArrays(data, count, length);
                var entries = new object[count];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = fixedBytes.Slice(i * length, length).ToArray();
                }

                return entries;
            default:
                throw new NotSupportedException(node.Type.ToString());
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException("File structure check failed: " + message);
        }
    }
}
