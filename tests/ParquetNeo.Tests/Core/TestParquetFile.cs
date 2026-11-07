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

    /// <summary>The pages of a column chunk, found by walking the headers from the chunk's first page.</summary>
    public List<PageInfo> Pages(int rowGroup, int column)
    {
        var chunk = Chunk(rowGroup, column);
        var pages = new List<PageInfo>();
        long position = chunk.DataPageOffset;
        long end = chunk.DataPageOffset + chunk.TotalCompressedSize;
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

    /// <summary>Decodes a whole column chunk; null entries are nulls. Values are int, long, float, double, bool or byte[].</summary>
    public object?[] ReadColumn(int rowGroup, int column)
    {
        var node = Schema.Columns[column];
        var codec = Registry.Get(Chunk(rowGroup, column).Codec);
        var result = new List<object?>();

        foreach (var page in Pages(rowGroup, column))
        {
            var header = page.Header;
            var compressed = Bytes.AsSpan((int)page.Offset + page.HeaderLength, header.CompressedPageSize);
            var data = new byte[header.UncompressedPageSize];
            codec.Decompress(compressed, data);

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
            var values = DecodeValues(node, data.AsSpan(position), present);
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
                Require(chunk.DataPageOffset == position, $"chunk {g}/{c} starts at {chunk.DataPageOffset}, expected {position}");
                Require(chunk.PathInSchema.SequenceEqual(Schema.Columns[c].Path), $"chunk {g}/{c} path");
                Require(chunk.Type == Schema.Columns[c].Type, $"chunk {g}/{c} type");
                Require(chunk.DictionaryPageOffset is null, "no dictionary pages are written yet");

                var pages = Pages(g, c);
                Require(pages.Count > 0, $"chunk {g}/{c} has no pages");
                long values = 0, chunkUncompressed = 0, chunkCompressed = 0;
                foreach (var page in pages)
                {
                    Require(page.Header.Type == PageType.DataPage, "only data page v1 is written");
                    Require(page.Header.DataPageHeader!.Encoding == ParquetEncoding.Plain, "values are PLAIN");
                    values += page.Header.DataPageHeader.NumValues;
                    chunkUncompressed += page.HeaderLength + page.Header.UncompressedPageSize;
                    chunkCompressed += page.HeaderLength + page.Header.CompressedPageSize;
                }

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
