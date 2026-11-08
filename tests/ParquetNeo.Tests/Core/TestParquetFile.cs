using System.Buffers.Binary;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Lz4;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;
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

    /// <summary>The level entries of a column chunk: one definition and repetition level each, and the values of the non-null ones.</summary>
    public sealed record ColumnEntries(int[] Definition, int[] Repetition, object[] Values)
    {
        /// <summary>The number of rows, which is the number of entries that start a new one.</summary>
        public int RowCount => Repetition.Count(level => level == 0);
    }

    /// <summary>Decodes a whole column chunk by its pages, independently of the library's reader.</summary>
    public ColumnEntries ReadEntries(int rowGroup, int column)
    {
        var node = Schema.Columns[column];
        var codec = Registry.Get(Chunk(rowGroup, column).Codec);
        var definition = new List<int>();
        var repetition = new List<int>();
        var values = new List<object>();
        object[]? dictionary = null;

        foreach (var page in Pages(rowGroup, column))
        {
            var header = page.Header;
            var stored = Bytes.AsSpan((int)page.Offset + page.HeaderLength, header.CompressedPageSize);
            var data = new byte[header.UncompressedPageSize];
            var v2 = header.DataPageHeaderV2;
            if (v2 is null)
            {
                codec.Decompress(stored, data);
            }
            else
            {
                // Data page v2: the levels are stored as they are and only the values are compressed.
                int levelBytes = v2.RepetitionLevelsByteLength + v2.DefinitionLevelsByteLength;
                stored[..levelBytes].CopyTo(data);
                if (v2.IsCompressed)
                {
                    codec.Decompress(stored[levelBytes..], data.AsSpan(levelBytes));
                }
                else
                {
                    stored[levelBytes..].CopyTo(data.AsSpan(levelBytes));
                }
            }

            if (header.Type == PageType.DictionaryPage)
            {
                dictionary = DecodeValues(node, data, header.DictionaryPageHeader!.NumValues);
                continue;
            }

            int entries = PageValueCount(header);
            var encoding = PageEncoding(header);
            int position = 0;
            var repetitionLevels = new int[entries]; // zeros when the column is not repeated: every entry is a row
            if (node.MaxRepetitionLevel > 0)
            {
                position = v2 is null
                    ? ReadLevels(data, position, repetitionLevels, node.MaxRepetitionLevel)
                    : ReadLevelsWithoutLength(data, position, v2.RepetitionLevelsByteLength, repetitionLevels, node.MaxRepetitionLevel);
            }

            var levels = Enumerable.Repeat(node.MaxDefinitionLevel, entries).ToArray(); // a required column has no level data: every entry is present
            if (node.MaxDefinitionLevel > 0)
            {
                position = v2 is null
                    ? ReadLevels(data, position, levels, node.MaxDefinitionLevel)
                    : ReadLevelsWithoutLength(data, position, v2.DefinitionLevelsByteLength, levels, node.MaxDefinitionLevel);
            }

            int present = levels.Count(l => l == node.MaxDefinitionLevel);
            var pageValues = encoding == ParquetEncoding.RleDictionary
                ? DecodeIndexed(dictionary ?? throw new InvalidDataException("A dictionary-encoded page without a dictionary page."), data.AsSpan(position), present)
                : DecodeValues(node, ToPlain(encoding, node, data.AsSpan(position), present), present);
            definition.AddRange(levels);
            repetition.AddRange(repetitionLevels);
            values.AddRange(pageValues);
        }

        return new ColumnEntries(definition.ToArray(), repetition.ToArray(), values.ToArray());
    }

    private static int PageValueCount(PageHeader header) => header.DataPageHeaderV2?.NumValues ?? header.DataPageHeader!.NumValues;

    private static ParquetEncoding PageEncoding(PageHeader header) => header.DataPageHeaderV2?.Encoding ?? header.DataPageHeader!.Encoding;

    private static int ReadLevelsWithoutLength(byte[] data, int position, int length, int[] levels, int maxLevel)
    {
        int width = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maxLevel);
        if (new RleBitPackedDecoder(data.AsSpan(position, length), width).ReadValues(levels) != levels.Length)
        {
            throw new InvalidDataException("Fewer levels than the page declares.");
        }

        return position + length;
    }

    private static int ReadLevels(byte[] data, int position, int[] levels, int maxLevel)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
        int width = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maxLevel);
        var decoder = new RleBitPackedDecoder(data.AsSpan(position + sizeof(int), length), width);
        if (decoder.ReadValues(levels) != levels.Length)
        {
            throw new InvalidDataException("Fewer levels than the page declares.");
        }

        return position + sizeof(int) + length;
    }

    /// <summary>Decodes a whole flat column chunk; null entries are nulls. Values are int, long, float, double, bool or byte[].</summary>
    public object?[] ReadColumn(int rowGroup, int column)
    {
        var node = Schema.Columns[column];
        if (node.MaxRepetitionLevel > 0)
        {
            throw new InvalidOperationException($"Column '{node.Name}' is repeated; use ReadEntries.");
        }

        var entries = ReadEntries(rowGroup, column);
        var result = new object?[entries.Definition.Length];
        int next = 0;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = entries.Definition[i] == node.MaxDefinitionLevel ? entries.Values[next++] : null;
        }

        return result;
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
                        Require(page.Header.Type is PageType.DataPage or PageType.DataPageV2, "only data pages are written");
                        var encoding = PageEncoding(page.Header);
                        Require(encoding != ParquetEncoding.PlainDictionary && encoding != ParquetEncoding.Rle, $"unexpected value encoding {encoding}");
                        if (encoding != ParquetEncoding.RleDictionary)
                        {
                            plainDataPages++;
                            seenPlain = true;
                        }
                        else
                        {
                            Require(hasDictionary && !seenPlain, "dictionary-encoded pages come first, and only in a chunk with a dictionary");
                            dictionaryDataPages++;
                        }

                        values += PageValueCount(page.Header);
                    }

                    chunkUncompressed += page.HeaderLength + page.Header.UncompressedPageSize;
                    chunkCompressed += page.HeaderLength + page.Header.CompressedPageSize;
                }

                Require(dictionaryPages == (hasDictionary ? 1 : 0), "at most one dictionary page");
                Require(chunk.EncodingStats is not null, "encoding_statistics are written");
                Require(EncodingStatisticsCount(chunk, PageType.DictionaryPage, ParquetEncoding.Plain) == dictionaryPages, "encoding_statistics for the dictionary page");
                Require(EncodingStatisticsCount(chunk, DataPageType(chunk), ParquetEncoding.RleDictionary) == dictionaryDataPages, "encoding_statistics for dictionary-encoded pages");
                Require(chunk.EncodingStats!.Where(e => e.PageType != PageType.DictionaryPage && e.Encoding != ParquetEncoding.RleDictionary).Sum(e => e.Count) == plainDataPages, "encoding_statistics for pages that are not dictionary encoded");
                foreach (var usedEncoding in pages.Where(p => p.Header.Type != PageType.DictionaryPage).Select(p => PageEncoding(p.Header)).Distinct())
                {
                    Require(chunk.Encodings.Contains(usedEncoding), $"encodings lists {usedEncoding}");
                }
                Require(chunk.Encodings.Contains(ParquetEncoding.RleDictionary) == dictionaryDataPages > 0, "encodings lists RLE_DICTIONARY exactly when it is used");
                Require(!hasDictionary || chunk.Encodings.Contains(ParquetEncoding.Plain), "PLAIN is listed when there is a dictionary page");

                Require(values == chunk.NumValues, $"chunk {g}/{c} holds {values} entries but declares {chunk.NumValues}");
                Require(ReadEntries(g, c).RowCount == group.NumRows, $"chunk {g}/{c} holds {ReadEntries(g, c).RowCount} rows, expected {group.NumRows}");
                if (Schema.Columns[c].MaxRepetitionLevel > 0)
                {
                    // Pages must not end in the middle of a row, so each one starts with a repetition level of 0.
                    foreach (var dataPage in pages.Where(p => p.Header.Type != PageType.DictionaryPage))
                    {
                        Require(PageStartsRow(dataPage, c, chunk), $"a page of chunk {g}/{c} starts in the middle of a row");
                    }
                }

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

    private bool PageStartsRow(PageInfo page, int column, ColumnMetaData chunk)
    {
        var node = Schema.Columns[column];
        var stored = Bytes.AsSpan((int)page.Offset + page.HeaderLength, page.Header.CompressedPageSize);
        var first = new int[1];
        int width = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)node.MaxRepetitionLevel);
        if (page.Header.DataPageHeaderV2 is { } v2)
        {
            // The repetition levels come first and are not compressed.
            new RleBitPackedDecoder(stored[..v2.RepetitionLevelsByteLength], width).ReadValues(first);
            return first[0] == 0;
        }

        var data = new byte[page.Header.UncompressedPageSize];
        Registry.Get(chunk.Codec).Decompress(stored, data);
        int length = BinaryPrimitives.ReadInt32LittleEndian(data);
        new RleBitPackedDecoder(data.AsSpan(sizeof(int), length), width).ReadValues(first);
        return first[0] == 0;
    }

    /// <summary>The type the page statistics of a chunk use for its data pages: DATA_PAGE_V2 when the chunk was written with that layout.</summary>
    private PageType DataPageType(ColumnMetaData chunk) =>
        chunk.EncodingStats!.Any(e => e.PageType == PageType.DataPageV2) ? PageType.DataPageV2 : PageType.DataPage;

    private static int EncodingStatisticsCount(ColumnMetaData chunk, PageType type, ParquetEncoding encoding) =>
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

    /// <summary>Turns the values of a page in a delta or byte-stream-split encoding into PLAIN layout; PLAIN stays as it is.</summary>
    private static byte[] ToPlain(ParquetEncoding encoding, PrimitiveNode node, ReadOnlySpan<byte> source, int count)
    {
        var plain = new System.Buffers.ArrayBufferWriter<byte>();
        int fixedLength = node.TypeLength ?? 0;
        switch (encoding)
        {
            case ParquetEncoding.Plain:
                return source.ToArray();
            case ParquetEncoding.DeltaBinaryPacked when node.Type == PhysicalType.Int32:
                var ints = new int[count];
                DeltaBinaryPackedDecoder.Decode(source, ints);
                PlainEncoder.WriteInt32s(ints, plain);
                break;
            case ParquetEncoding.DeltaBinaryPacked:
                var longs = new long[count];
                DeltaBinaryPackedDecoder.Decode(source, longs);
                PlainEncoder.WriteInt64s(longs, plain);
                break;
            case ParquetEncoding.DeltaLengthByteArray:
                DeltaLengthByteArrayDecoder.Decode(source, count, plain);
                break;
            case ParquetEncoding.DeltaByteArray:
                DeltaByteArrayDecoder.Decode(source, count, node.Type == PhysicalType.FixedLenByteArray ? fixedLength : 0, 1 << 28, plain);
                break;
            case ParquetEncoding.ByteStreamSplit:
                int width = node.Type switch { PhysicalType.Int32 or PhysicalType.Float => 4, PhysicalType.Int64 or PhysicalType.Double => 8, _ => fixedLength };
                var bytes = new byte[count * width];
                ByteStreamSplitDecoder.Decode(source, count, width, bytes);
                return bytes;
            default:
                throw new NotSupportedException(encoding.ToString());
        }

        return plain.WrittenSpan.ToArray();
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
