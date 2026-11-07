using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using System.Text;

namespace ParquetNeo.Core;

/// <summary>
/// Writes the values of one column of a row group. Obtain it from <see cref="RowGroupWriter.NextColumn"/>, call
/// one of the <c>Write…</c> methods one or more times, then move on to the next column.
/// </summary>
/// <remarks>
/// <para>
/// For a required column pass only the values. For an optional column also pass one definition level per row:
/// <c>0</c> for a null and <c>1</c> for a value, with <c>values</c> holding just the non-null values in order.
/// </para>
/// <para>Values are buffered into pages of about <see cref="ParquetWriterOptions.DataPageSize"/> bytes, which are
/// compressed and written to the stream as they fill. Instances are not thread safe.</para>
/// </remarks>
public sealed class ColumnWriter
{
    /// <summary>
    /// A page also closes after this many rows. A page of nulls takes almost no bytes, so without a row limit an
    /// all-null column would become one enormous page that readers must hold the levels of in memory.
    /// </summary>
    internal const int MaxRowsPerPage = 1 << 20;

    private readonly ParquetFileWriter _file;
    private readonly WriterBuffers _buffers;
    private readonly ICodec _codec;
    private readonly int _pageSize;
    private readonly long _dictionaryLimit;
    private readonly int _maxDefinitionLevel;
    private readonly int _levelBitWidth;

    // Non-null while the chunk is dictionary encoded. Its data pages are encoded as they fill but held back,
    // because the dictionary page, which is only complete at the end, has to come first in the file.
    private DictionaryBuilder? _dictionary;
    private readonly List<EncodedPage> _pending = new();
    private byte[]? _stringScratch;
    private readonly StatisticsCollector? _stats;
    private long _valueCount; // non-null values written, so that rows minus values is the null count

    private int _pageLevelCount;
    private int _pageLogicalBytes; // what the page's values would take PLAIN encoded, used to decide when a page is full
    private long _rows;
    private long _firstPageOffset = -1;
    private long _dataPageOffset = -1;
    private long _dictionaryPageOffset = -1;
    private long _totalUncompressed;
    private long _totalCompressed;
    private int _dictionaryPages;
    private int _dictionaryEncodedPages;
    private int _plainPages;
    private bool _completed;

    internal ColumnWriter(ParquetFileWriter file, PrimitiveNode column, ICodec codec, ParquetWriterOptions options)
    {
        _file = file;
        _buffers = file.Buffers;
        Column = column;
        _codec = codec;
        _pageSize = options.DataPageSize;
        _dictionaryLimit = options.DictionaryPageSizeLimit;
        _maxDefinitionLevel = column.MaxDefinitionLevel;
        _levelBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxDefinitionLevel);
        _dictionary = options.DictionaryEnabled ? CreateDictionary(column.Type, column.TypeLength) : null;
        _stats = StatisticsFactory.Create(column, options);
        _buffers.Reset();
    }

    /// <summary>The column this writer fills.</summary>
    public PrimitiveNode Column { get; }

    /// <summary>The number of rows written so far.</summary>
    public long RowCount => _rows;

    private bool HasLevels => _maxDefinitionLevel > 0;

    private int RowsFree => MaxRowsPerPage - _pageLevelCount;

    private int BufferedBytes => Column.Type == PhysicalType.Boolean ? (_buffers.Booleans.WrittenCount + 7) / 8 : _buffers.Values.WrittenCount;

    /// <summary>How full the current page is, in the bytes its values take PLAIN encoded (even when they are dictionary encoded).</summary>
    private int PageBytes => _dictionary is not null ? _pageLogicalBytes : BufferedBytes;

    public void WriteBatch(ReadOnlySpan<int> values, ReadOnlySpan<int> definitionLevels = default) =>
        WriteFixedWidth<int, Int32Encoder>(PhysicalType.Int32, values, definitionLevels, sizeof(int));

    public void WriteBatch(ReadOnlySpan<long> values, ReadOnlySpan<int> definitionLevels = default) =>
        WriteFixedWidth<long, Int64Encoder>(PhysicalType.Int64, values, definitionLevels, sizeof(long));

    public void WriteBatch(ReadOnlySpan<float> values, ReadOnlySpan<int> definitionLevels = default) =>
        WriteFixedWidth<float, SingleEncoder>(PhysicalType.Float, values, definitionLevels, sizeof(float));

    public void WriteBatch(ReadOnlySpan<double> values, ReadOnlySpan<int> definitionLevels = default) =>
        WriteFixedWidth<double, DoubleEncoder>(PhysicalType.Double, values, definitionLevels, sizeof(double));

    public void WriteBatch(ReadOnlySpan<bool> values, ReadOnlySpan<int> definitionLevels = default)
    {
        int rows = PrepareBatch(PhysicalType.Boolean, values.Length, definitionLevels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            // Booleans are bit-packed per page, so they are collected here and packed when the page closes.
            long remainingBits = Math.Max(1L, (long)(_pageSize - PageBytes) * 8);
            int take = (int)Math.Min(Math.Min(rows - row, remainingBits), RowsFree);
            int valueCount = AppendLevels(definitionLevels, row, take);
            _buffers.Booleans.Write(values.Slice(next, valueCount));
            _stats?.Add(values.Slice(next, valueCount));
            _valueCount += valueCount;
            CompleteChunk(take);
            row += take;
            next += valueCount;
        }
    }

    /// <summary>Writes BYTE_ARRAY values; the memory of each value is read immediately and not retained.</summary>
    public void WriteByteArrays(ReadOnlySpan<ReadOnlyMemory<byte>> values, ReadOnlySpan<int> definitionLevels = default)
    {
        int rows = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            int start = row;
            while (row < rows)
            {
                if (!HasLevels || definitionLevels[row] == _maxDefinitionLevel)
                {
                    AppendByteArray(values[next++].Span);
                }

                row++;
                if (PageBytes >= _pageSize || _pageLevelCount + (row - start) >= MaxRowsPerPage)
                {
                    break;
                }
            }

            AppendLevelsAndComplete(definitionLevels, start, row - start);
        }
    }

    /// <summary>Writes BYTE_ARRAY values from strings, encoded as UTF-8 straight into the page buffer.</summary>
    public void WriteStrings(ReadOnlySpan<string> values, ReadOnlySpan<int> definitionLevels = default)
    {
        int rows = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            int start = row;
            while (row < rows)
            {
                if (!HasLevels || definitionLevels[row] == _maxDefinitionLevel)
                {
                    AppendString(values[next++]);
                }

                row++;
                if (PageBytes >= _pageSize || _pageLevelCount + (row - start) >= MaxRowsPerPage)
                {
                    break;
                }
            }

            AppendLevelsAndComplete(definitionLevels, start, row - start);
        }
    }

    /// <summary>Writes FIXED_LEN_BYTE_ARRAY values given back to back; the length must be a multiple of the column's type length.</summary>
    public void WriteFixedLengthByteArrays(ReadOnlySpan<byte> values, ReadOnlySpan<int> definitionLevels = default)
    {
        int length = Column.TypeLength ?? 0;
        if (length == 0 || values.Length % length != 0)
        {
            throw new ArgumentException($"The values must be a whole number of {length}-byte entries.", nameof(values));
        }

        int rows = PrepareBatch(PhysicalType.FixedLenByteArray, values.Length / length, definitionLevels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            int take = (int)Math.Min(Math.Min(rows - row, Math.Max(1, (_pageSize - PageBytes) / length)), RowsFree);
            int valueCount = AppendLevels(definitionLevels, row, take);
            var chunk = values.Slice(next * length, valueCount * length);
            if (_stats is not null)
            {
                for (int i = 0; i < valueCount; i++)
                {
                    _stats.AddBinary(chunk.Slice(i * length, length));
                }
            }

            _valueCount += valueCount;
            if (_dictionary is BinaryDictionary dictionary)
            {
                for (int i = 0; i < valueCount; i++)
                {
                    AddIndex(dictionary.GetOrAdd(chunk.Slice(i * length, length)));
                }

                _pageLogicalBytes += chunk.Length;
            }
            else
            {
                _buffers.Values.Write(chunk);
            }

            CompleteChunk(take);
            row += take;
            next += valueCount;
        }
    }

    internal ColumnChunk Complete()
    {
        EnsureOpen();
        FlushPage();
        if (_dictionary is not null)
        {
            // The chunk stayed dictionary encoded to the end: the dictionary page and the held-back pages go out now.
            if (_rows > 0)
            {
                WriteDictionaryChunk();
            }

            _dictionary = null;
        }

        _completed = true;
        if (_rows == 0)
        {
            throw new InvalidOperationException($"No values were written to column '{Column.Name}'.");
        }

        var encodings = new List<ParquetEncoding> { ParquetEncoding.Plain };
        if (HasLevels)
        {
            encodings.Add(ParquetEncoding.Rle);
        }

        var pageStats = new List<PageEncodingStats>();
        if (_dictionaryPages > 0)
        {
            pageStats.Add(new PageEncodingStats { PageType = PageType.DictionaryPage, Encoding = ParquetEncoding.Plain, Count = _dictionaryPages });
        }

        if (_dictionaryEncodedPages > 0)
        {
            encodings.Add(ParquetEncoding.RleDictionary);
            pageStats.Add(new PageEncodingStats { PageType = PageType.DataPage, Encoding = ParquetEncoding.RleDictionary, Count = _dictionaryEncodedPages });
        }

        if (_plainPages > 0)
        {
            pageStats.Add(new PageEncodingStats { PageType = PageType.DataPage, Encoding = ParquetEncoding.Plain, Count = _plainPages });
        }

        return new ColumnChunk
        {
            FileOffset = 0, // deprecated; 0 when no ColumnMetaData is written outside the footer
            MetaData = new ColumnMetaData
            {
                Type = Column.Type,
                Encodings = encodings,
                PathInSchema = Column.Path.ToList(),
                Codec = _codec.Id,
                NumValues = _rows,
                TotalUncompressedSize = _totalUncompressed,
                TotalCompressedSize = _totalCompressed,
                DataPageOffset = _dataPageOffset,
                DictionaryPageOffset = _dictionaryPageOffset >= 0 ? _dictionaryPageOffset : null,
                EncodingStats = pageStats,
                Statistics = _stats?.Build(_rows - _valueCount),
            },
        };
    }

    internal long TotalUncompressedSize => _totalUncompressed;

    internal long TotalCompressedSize => _totalCompressed;

    /// <summary>Where the chunk starts: its dictionary page if it has one, otherwise its first data page.</summary>
    internal long FirstPageOffset => _firstPageOffset;

    private void WriteFixedWidth<T, TEncoder>(PhysicalType expected, ReadOnlySpan<T> values, ReadOnlySpan<int> levels, int valueBytes)
        where T : unmanaged
        where TEncoder : IPlainValueEncoder<T>
    {
        int rows = PrepareBatch(expected, values.Length, levels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            // As many rows as fit in the rest of the page (at least one), so pages close close to the target size.
            int take = (int)Math.Min(Math.Min(rows - row, Math.Max(1, (_pageSize - PageBytes) / valueBytes)), RowsFree);
            int valueCount = AppendLevels(levels, row, take);
            var chunk = values.Slice(next, valueCount);
            _stats?.Add(chunk);
            _valueCount += valueCount;
            if (_dictionary is not null)
            {
                TEncoder.AddToDictionary(chunk, _dictionary, _buffers.Indices);
                _pageLogicalBytes += valueCount * valueBytes;
            }
            else
            {
                TEncoder.Write(chunk, _buffers.Values);
            }

            CompleteChunk(take);
            row += take;
            next += valueCount;
        }
    }

    private void AppendByteArray(ReadOnlySpan<byte> value)
    {
        _stats?.AddBinary(value);
        _valueCount++;
        if (_dictionary is BinaryDictionary dictionary)
        {
            AddIndex(dictionary.GetOrAdd(value));
            _pageLogicalBytes += sizeof(int) + value.Length;
        }
        else
        {
            PlainEncoder.WriteByteArray(value, _buffers.Values);
        }
    }

    private void AppendString(string value)
    {
        _valueCount++;
        if (_dictionary is not BinaryDictionary dictionary)
        {
            int start = _buffers.Values.WrittenCount;
            PlainEncoder.WriteString(value, _buffers.Values);
            if (_stats is not null)
            {
                // The string was encoded straight into the page buffer; the statistics read it back from there.
                _stats.AddBinary(_buffers.Values.WrittenSpan[(start + sizeof(int))..]);
            }

            return;
        }

        // A dictionary is keyed by bytes (strings and byte arrays share it), so encode before the lookup.
        int length = Encoding.UTF8.GetByteCount(value);
        if (_stringScratch is null || _stringScratch.Length < length)
        {
            _stringScratch = new byte[Math.Max(length, 256)];
        }

        Encoding.UTF8.GetBytes(value, _stringScratch);
        _stats?.AddBinary(_stringScratch.AsSpan(0, length));
        AddIndex(dictionary.GetOrAdd(_stringScratch.AsSpan(0, length)));
        _pageLogicalBytes += sizeof(int) + length;
    }

    private void AddIndex(int index)
    {
        var span = _buffers.Indices.GetSpan(1);
        span[0] = index;
        _buffers.Indices.Advance(1);
    }

    /// <summary>Checks a batch and returns its number of rows.</summary>
    private int PrepareBatch(PhysicalType expected, int valueCount, ReadOnlySpan<int> definitionLevels)
    {
        EnsureOpen();
        if (Column.Type != expected)
        {
            throw new ArgumentException($"Column '{Column.Name}' is {Column.Type}, so it cannot take {expected} values.");
        }

        if (!HasLevels)
        {
            if (!definitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is required, so it takes no definition levels.", nameof(definitionLevels));
            }

            return valueCount;
        }

        if (definitionLevels.IsEmpty)
        {
            if (valueCount != 0)
            {
                throw new ArgumentException($"Column '{Column.Name}' is optional, so it needs one definition level per row.", nameof(definitionLevels));
            }

            return 0;
        }

        if (definitionLevels.IndexOfAnyExceptInRange(0, _maxDefinitionLevel) >= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definitionLevels), $"Definition levels of column '{Column.Name}' must be between 0 and {_maxDefinitionLevel}.");
        }

        int present = definitionLevels.Count(_maxDefinitionLevel);
        if (present != valueCount)
        {
            throw new ArgumentException($"{present} rows are marked non-null but {valueCount} values were given.", nameof(definitionLevels));
        }

        return definitionLevels.Length;
    }

    /// <summary>Buffers the levels of rows [<paramref name="row"/>, +<paramref name="take"/>) and returns how many of them hold a value.</summary>
    private int AppendLevels(ReadOnlySpan<int> definitionLevels, int row, int take)
    {
        if (!HasLevels)
        {
            return take;
        }

        var slice = definitionLevels.Slice(row, take);
        _buffers.Levels.Write(slice);
        return slice.Count(_maxDefinitionLevel);
    }

    private void AppendLevelsAndComplete(ReadOnlySpan<int> definitionLevels, int start, int count)
    {
        if (HasLevels)
        {
            _buffers.Levels.Write(definitionLevels.Slice(start, count));
        }

        CompleteChunk(count);
    }

    private void CompleteChunk(int levelCount)
    {
        _pageLevelCount += levelCount;
        _rows += levelCount;
        if (PageBytes >= _pageSize || _pageLevelCount >= MaxRowsPerPage)
        {
            FlushPage();
        }

        if (_dictionary is not null && _dictionary.EncodedSize > _dictionaryLimit)
        {
            FallBackToPlain();
        }
    }

    /// <summary>The dictionary has grown too big: write it out with the pages that use it, and continue with plain pages.</summary>
    private void FallBackToPlain()
    {
        FlushPage();
        WriteDictionaryChunk();
        _dictionary = null;
    }

    private void FlushPage()
    {
        if (_pageLevelCount == 0)
        {
            return;
        }

        // Data page v1: [4-byte length][RLE definition levels][values]; a required column has only values.
        Span<byte> prefixStorage = stackalloc byte[sizeof(int)];
        scoped ReadOnlySpan<byte> levelPrefix = default;
        var levelData = ReadOnlySpan<byte>.Empty;
        if (HasLevels)
        {
            RleBitPackedEncoder.Encode(_buffers.Levels.WrittenSpan, _levelBitWidth, _buffers.LevelBytes);
            levelData = _buffers.LevelBytes.WrittenSpan;
            BinaryPrimitives.WriteInt32LittleEndian(prefixStorage, levelData.Length);
            levelPrefix = prefixStorage;
        }

        if (_dictionary is not null)
        {
            HoldDictionaryEncodedPage(levelPrefix, levelData);
        }
        else
        {
            WritePlainPage(levelPrefix, levelData);
        }

        _pageLevelCount = 0;
        _pageLogicalBytes = 0;
        _buffers.Reset();
    }

    /// <summary>Encodes a page whose values are dictionary indexes and keeps it until the dictionary page can be written.</summary>
    private void HoldDictionaryEncodedPage(ReadOnlySpan<byte> levelPrefix, ReadOnlySpan<byte> levelData)
    {
        int bitWidth = IndexBitWidth(_dictionary!.Count);
        var body = _buffers.Values; // free in dictionary mode: nothing is written PLAIN
        body.GetSpan(1)[0] = (byte)bitWidth;
        body.Advance(1);
        RleBitPackedEncoder.Encode(_buffers.Indices.WrittenSpan, bitWidth, body);

        _pending.Add(BuildPage(PageType.DataPage, _pageLevelCount, ParquetEncoding.RleDictionary, levelPrefix, levelData, body.WrittenSpan));
        _dictionaryEncodedPages++;
    }

    private void WritePlainPage(ReadOnlySpan<byte> levelPrefix, ReadOnlySpan<byte> levelData)
    {
        if (Column.Type == PhysicalType.Boolean)
        {
            PlainEncoder.WriteBooleans(_buffers.Booleans.WrittenSpan, _buffers.Values);
        }

        var values = _buffers.Values.WrittenSpan;
        long uncompressedLength = levelPrefix.Length + levelData.Length + (long)values.Length;
        if (uncompressedLength > int.MaxValue)
        {
            throw new InvalidOperationException("A data page cannot exceed 2 GiB.");
        }

        int uncompressedSize = (int)uncompressedLength;
        if (_firstPageOffset < 0)
        {
            _firstPageOffset = _file.Position;
        }

        if (_dataPageOffset < 0)
        {
            _dataPageOffset = _file.Position;
        }

        if (_codec.Id == CompressionCodec.Uncompressed)
        {
            // No copy needed: the three parts go straight to the stream.
            int headerLength = WriteHeader(PageType.DataPage, _pageLevelCount, ParquetEncoding.Plain, uncompressedSize, uncompressedSize);
            _file.Write(_buffers.Header.WrittenSpan);
            _file.Write(levelPrefix);
            _file.Write(levelData);
            _file.Write(values);
            Account(headerLength, uncompressedSize, uncompressedSize);
        }
        else
        {
            WriteEncoded(BuildPage(PageType.DataPage, _pageLevelCount, ParquetEncoding.Plain, levelPrefix, levelData, values));
        }

        _plainPages++;
    }

    /// <summary>Writes the dictionary page followed by the data pages that were held back for it.</summary>
    private void WriteDictionaryChunk()
    {
        _buffers.Values.ResetWrittenCount();
        _dictionary!.WritePlain(_buffers.Values);
        var dictionaryPage = BuildPage(PageType.DictionaryPage, _dictionary.Count, ParquetEncoding.Plain, default, default, _buffers.Values.WrittenSpan);

        _firstPageOffset = _dictionaryPageOffset = _file.Position;
        WriteEncoded(dictionaryPage);
        _dictionaryPages = 1;

        _dataPageOffset = _file.Position;
        foreach (var page in _pending)
        {
            WriteEncoded(page);
        }

        _pending.Clear();
        _buffers.Values.ResetWrittenCount();
    }

    /// <summary>Compresses a page body and returns the page, header and body, in one pooled buffer.</summary>
    private EncodedPage BuildPage(
        PageType type,
        int numValues,
        ParquetEncoding encoding,
        ReadOnlySpan<byte> levelPrefix,
        ReadOnlySpan<byte> levelData,
        ReadOnlySpan<byte> values)
    {
        long total = levelPrefix.Length + levelData.Length + (long)values.Length;
        if (total > int.MaxValue)
        {
            throw new InvalidOperationException("A page cannot exceed 2 GiB.");
        }

        int uncompressedSize = (int)total;
        var pool = ArrayPool<byte>.Shared;
        byte[]? compressed = null;
        int compressedSize = uncompressedSize;
        if (_codec.Id != CompressionCodec.Uncompressed)
        {
            byte[] source = pool.Rent(uncompressedSize);
            compressed = pool.Rent(_codec.GetMaxCompressedLength(uncompressedSize));
            try
            {
                levelPrefix.CopyTo(source);
                levelData.CopyTo(source.AsSpan(levelPrefix.Length));
                values.CopyTo(source.AsSpan(levelPrefix.Length + levelData.Length));
                compressedSize = _codec.Compress(source.AsSpan(0, uncompressedSize), compressed);
            }
            finally
            {
                pool.Return(source);
            }
        }

        int headerLength = WriteHeader(type, numValues, encoding, uncompressedSize, compressedSize);
        byte[] page = pool.Rent(headerLength + compressedSize);
        _buffers.Header.WrittenSpan.CopyTo(page);
        if (compressed is null)
        {
            levelPrefix.CopyTo(page.AsSpan(headerLength));
            levelData.CopyTo(page.AsSpan(headerLength + levelPrefix.Length));
            values.CopyTo(page.AsSpan(headerLength + levelPrefix.Length + levelData.Length));
        }
        else
        {
            compressed.AsSpan(0, compressedSize).CopyTo(page.AsSpan(headerLength));
            pool.Return(compressed);
        }

        return new EncodedPage(page, headerLength + compressedSize, headerLength, uncompressedSize, compressedSize);
    }

    private void WriteEncoded(EncodedPage page)
    {
        _file.Write(page.Buffer.AsSpan(0, page.Length));
        Account(page.HeaderLength, page.UncompressedSize, page.CompressedSize);
        ArrayPool<byte>.Shared.Return(page.Buffer);
    }

    private int WriteHeader(PageType type, int numValues, ParquetEncoding encoding, int uncompressedSize, int compressedSize)
    {
        _buffers.Header.ResetWrittenCount();
        var header = new PageHeader
        {
            Type = type,
            UncompressedPageSize = uncompressedSize,
            CompressedPageSize = compressedSize,
        };

        if (type == PageType.DictionaryPage)
        {
            header.DictionaryPageHeader = new DictionaryPageHeader { NumValues = numValues, Encoding = encoding };
        }
        else
        {
            header.DataPageHeader = new DataPageHeader
            {
                NumValues = numValues,
                Encoding = encoding,
                DefinitionLevelEncoding = ParquetEncoding.Rle,
                RepetitionLevelEncoding = ParquetEncoding.Rle,
            };
        }

        header.Serialize(_buffers.Header);
        return _buffers.Header.WrittenCount;
    }

    private void Account(int headerLength, int uncompressedSize, int compressedSize)
    {
        // The chunk sizes in the metadata include the page headers.
        _totalUncompressed += headerLength + uncompressedSize;
        _totalCompressed += headerLength + compressedSize;
    }

    /// <summary>The bits needed for the largest index of a dictionary of <paramref name="count"/> entries (at least 1, which every reader handles).</summary>
    private static int IndexBitWidth(int count) => count <= 1 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)(count - 1));

    private static DictionaryBuilder? CreateDictionary(PhysicalType type, int? typeLength) => type switch
    {
        // Floating-point values go in as their bit patterns, which keeps -0.0, +0.0 and every NaN distinct.
        PhysicalType.Int32 or PhysicalType.Float => new FixedDictionary<int>(PlainEncoder.WriteInt32s),
        PhysicalType.Int64 or PhysicalType.Double => new FixedDictionary<long>(PlainEncoder.WriteInt64s),
        PhysicalType.ByteArray => new BinaryDictionary(lengthPrefixed: true),
        PhysicalType.FixedLenByteArray when typeLength > 0 => new BinaryDictionary(lengthPrefixed: false),
        _ => null, // booleans have a more compact encoding of their own
    };

    private void EnsureOpen()
    {
        if (_completed)
        {
            throw new InvalidOperationException($"Column '{Column.Name}' is already complete; this writer can no longer be used.");
        }

        _file.EnsureUsable();
    }

    private readonly record struct EncodedPage(byte[] Buffer, int Length, int HeaderLength, int UncompressedSize, int CompressedSize);

    private interface IPlainValueEncoder<T>
    {
        static abstract void Write(ReadOnlySpan<T> values, IBufferWriter<byte> output);

        static abstract void AddToDictionary(ReadOnlySpan<T> values, DictionaryBuilder dictionary, ArrayBufferWriter<int> indexes);
    }

    private readonly struct Int32Encoder : IPlainValueEncoder<int>
    {
        public static void Write(ReadOnlySpan<int> values, IBufferWriter<byte> output) => PlainEncoder.WriteInt32s(values, output);

        public static void AddToDictionary(ReadOnlySpan<int> values, DictionaryBuilder dictionary, ArrayBufferWriter<int> indexes) =>
            ((FixedDictionary<int>)dictionary).Add(values, indexes);
    }

    private readonly struct Int64Encoder : IPlainValueEncoder<long>
    {
        public static void Write(ReadOnlySpan<long> values, IBufferWriter<byte> output) => PlainEncoder.WriteInt64s(values, output);

        public static void AddToDictionary(ReadOnlySpan<long> values, DictionaryBuilder dictionary, ArrayBufferWriter<int> indexes) =>
            ((FixedDictionary<long>)dictionary).Add(values, indexes);
    }

    private readonly struct SingleEncoder : IPlainValueEncoder<float>
    {
        public static void Write(ReadOnlySpan<float> values, IBufferWriter<byte> output) => PlainEncoder.WriteSingles(values, output);

        public static void AddToDictionary(ReadOnlySpan<float> values, DictionaryBuilder dictionary, ArrayBufferWriter<int> indexes) =>
            ((FixedDictionary<int>)dictionary).Add(MemoryMarshal.Cast<float, int>(values), indexes);
    }

    private readonly struct DoubleEncoder : IPlainValueEncoder<double>
    {
        public static void Write(ReadOnlySpan<double> values, IBufferWriter<byte> output) => PlainEncoder.WriteDoubles(values, output);

        public static void AddToDictionary(ReadOnlySpan<double> values, DictionaryBuilder dictionary, ArrayBufferWriter<int> indexes) =>
            ((FixedDictionary<long>)dictionary).Add(MemoryMarshal.Cast<double, long>(values), indexes);
    }
}
