using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

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
    private readonly int _maxDefinitionLevel;
    private readonly int _levelBitWidth;

    private int _pageLevelCount;
    private int _pageValueCount;
    private long _rows;
    private long _firstPageOffset = -1;
    private long _totalUncompressed;
    private long _totalCompressed;
    private bool _completed;

    internal ColumnWriter(ParquetFileWriter file, PrimitiveNode column, ICodec codec, int pageSize)
    {
        _file = file;
        _buffers = file.Buffers;
        Column = column;
        _codec = codec;
        _pageSize = pageSize;
        _maxDefinitionLevel = column.MaxDefinitionLevel;
        _levelBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxDefinitionLevel);
        _buffers.Reset();
    }

    /// <summary>The column this writer fills.</summary>
    public PrimitiveNode Column { get; }

    /// <summary>The number of rows written so far.</summary>
    public long RowCount => _rows;

    private bool HasLevels => _maxDefinitionLevel > 0;

    private int RowsFree => MaxRowsPerPage - _pageLevelCount;

    private int BufferedBytes => Column.Type == PhysicalType.Boolean ? (_buffers.Booleans.WrittenCount + 7) / 8 : _buffers.Values.WrittenCount;

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
            long remainingBits = Math.Max(1L, (long)(_pageSize - BufferedBytes) * 8);
            int take = (int)Math.Min(Math.Min(rows - row, remainingBits), RowsFree);
            int valueCount = AppendLevels(definitionLevels, row, take);
            _buffers.Booleans.Write(values.Slice(next, valueCount));
            CompleteChunk(take, valueCount);
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
            int valuesBefore = next;
            while (row < rows)
            {
                if (!HasLevels || definitionLevels[row] == _maxDefinitionLevel)
                {
                    PlainEncoder.WriteByteArray(values[next++].Span, _buffers.Values);
                }

                row++;
                if (_buffers.Values.WrittenCount >= _pageSize || _pageLevelCount + (row - start) >= MaxRowsPerPage)
                {
                    break;
                }
            }

            if (HasLevels)
            {
                _buffers.Levels.Write(definitionLevels.Slice(start, row - start));
            }

            CompleteChunk(row - start, next - valuesBefore);
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
            int valuesBefore = next;
            while (row < rows)
            {
                if (!HasLevels || definitionLevels[row] == _maxDefinitionLevel)
                {
                    PlainEncoder.WriteString(values[next++], _buffers.Values);
                }

                row++;
                if (_buffers.Values.WrittenCount >= _pageSize || _pageLevelCount + (row - start) >= MaxRowsPerPage)
                {
                    break;
                }
            }

            if (HasLevels)
            {
                _buffers.Levels.Write(definitionLevels.Slice(start, row - start));
            }

            CompleteChunk(row - start, next - valuesBefore);
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
            int take = (int)Math.Min(Math.Min(rows - row, Math.Max(1, (_pageSize - BufferedBytes) / length)), RowsFree);
            int valueCount = AppendLevels(definitionLevels, row, take);
            _buffers.Values.Write(values.Slice(next * length, valueCount * length));
            CompleteChunk(take, valueCount);
            row += take;
            next += valueCount;
        }
    }

    internal ColumnChunk Complete()
    {
        EnsureOpen();
        FlushPage();
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
                DataPageOffset = _firstPageOffset,
            },
        };
    }

    internal long TotalUncompressedSize => _totalUncompressed;

    internal long TotalCompressedSize => _totalCompressed;

    internal long FirstPageOffset => _firstPageOffset;

    private void WriteFixedWidth<T, TEncoder>(PhysicalType expected, ReadOnlySpan<T> values, ReadOnlySpan<int> levels, int valueBytes)
        where TEncoder : IPlainValueEncoder<T>
    {
        int rows = PrepareBatch(expected, values.Length, levels);
        int row = 0;
        int next = 0;
        while (row < rows)
        {
            // As many rows as fit in the rest of the page (at least one), so pages close close to the target size.
            int take = (int)Math.Min(Math.Min(rows - row, Math.Max(1, (_pageSize - BufferedBytes) / valueBytes)), RowsFree);
            int valueCount = AppendLevels(levels, row, take);
            TEncoder.Write(values.Slice(next, valueCount), _buffers.Values);
            CompleteChunk(take, valueCount);
            row += take;
            next += valueCount;
        }
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

    private void CompleteChunk(int levelCount, int valueCount)
    {
        _pageLevelCount += levelCount;
        _pageValueCount += valueCount;
        _rows += levelCount;
        if (BufferedBytes >= _pageSize || _pageLevelCount >= MaxRowsPerPage)
        {
            FlushPage();
        }
    }

    private void FlushPage()
    {
        if (_pageLevelCount == 0)
        {
            return;
        }

        if (Column.Type == PhysicalType.Boolean)
        {
            PlainEncoder.WriteBooleans(_buffers.Booleans.WrittenSpan, _buffers.Values);
        }

        // Data page v1: [4-byte length][RLE definition levels][PLAIN values]; a required column has only values.
        var levelData = ReadOnlySpan<byte>.Empty;
        Span<byte> levelPrefix = stackalloc byte[sizeof(int)];
        if (HasLevels)
        {
            RleBitPackedEncoder.Encode(_buffers.Levels.WrittenSpan, _levelBitWidth, _buffers.LevelBytes);
            levelData = _buffers.LevelBytes.WrittenSpan;
            BinaryPrimitives.WriteInt32LittleEndian(levelPrefix, levelData.Length);
        }
        else
        {
            levelPrefix = default;
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

        if (_codec.Id == CompressionCodec.Uncompressed)
        {
            int headerLength = WriteHeader(uncompressedSize, uncompressedSize);
            _file.Write(_buffers.Header.WrittenSpan);
            _file.Write(levelPrefix);
            _file.Write(levelData);
            _file.Write(values);
            Account(headerLength, uncompressedSize, uncompressedSize);
        }
        else
        {
            WriteCompressedPage(levelPrefix, levelData, values, uncompressedSize);
        }

        _pageLevelCount = 0;
        _pageValueCount = 0;
        _buffers.Reset();
    }

    private void WriteCompressedPage(ReadOnlySpan<byte> levelPrefix, ReadOnlySpan<byte> levelData, ReadOnlySpan<byte> values, int uncompressedSize)
    {
        var pool = ArrayPool<byte>.Shared;
        byte[] source = pool.Rent(uncompressedSize);
        byte[] target = pool.Rent(_codec.GetMaxCompressedLength(uncompressedSize));
        try
        {
            levelPrefix.CopyTo(source);
            levelData.CopyTo(source.AsSpan(levelPrefix.Length));
            values.CopyTo(source.AsSpan(levelPrefix.Length + levelData.Length));

            int compressedSize = _codec.Compress(source.AsSpan(0, uncompressedSize), target);
            int headerLength = WriteHeader(uncompressedSize, compressedSize);
            _file.Write(_buffers.Header.WrittenSpan);
            _file.Write(target.AsSpan(0, compressedSize));
            Account(headerLength, uncompressedSize, compressedSize);
        }
        finally
        {
            pool.Return(source);
            pool.Return(target);
        }
    }

    private int WriteHeader(int uncompressedSize, int compressedSize)
    {
        _buffers.Header.ResetWrittenCount();
        new PageHeader
        {
            Type = PageType.DataPage,
            UncompressedPageSize = uncompressedSize,
            CompressedPageSize = compressedSize,
            DataPageHeader = new DataPageHeader
            {
                NumValues = _pageLevelCount,
                Encoding = ParquetEncoding.Plain,
                DefinitionLevelEncoding = ParquetEncoding.Rle,
                RepetitionLevelEncoding = ParquetEncoding.Rle,
            },
        }.Serialize(_buffers.Header);
        return _buffers.Header.WrittenCount;
    }

    private void Account(int headerLength, int uncompressedSize, int compressedSize)
    {
        // The chunk sizes in the metadata include the page headers.
        _totalUncompressed += headerLength + uncompressedSize;
        _totalCompressed += headerLength + compressedSize;
    }

    private void EnsureOpen()
    {
        if (_completed)
        {
            throw new InvalidOperationException($"Column '{Column.Name}' is already complete; this writer can no longer be used.");
        }

        _file.EnsureUsable();
    }

    private interface IPlainValueEncoder<T>
    {
        static abstract void Write(ReadOnlySpan<T> values, IBufferWriter<byte> output);
    }

    private readonly struct Int32Encoder : IPlainValueEncoder<int>
    {
        public static void Write(ReadOnlySpan<int> values, IBufferWriter<byte> output) => PlainEncoder.WriteInt32s(values, output);
    }

    private readonly struct Int64Encoder : IPlainValueEncoder<long>
    {
        public static void Write(ReadOnlySpan<long> values, IBufferWriter<byte> output) => PlainEncoder.WriteInt64s(values, output);
    }

    private readonly struct SingleEncoder : IPlainValueEncoder<float>
    {
        public static void Write(ReadOnlySpan<float> values, IBufferWriter<byte> output) => PlainEncoder.WriteSingles(values, output);
    }

    private readonly struct DoubleEncoder : IPlainValueEncoder<double>
    {
        public static void Write(ReadOnlySpan<double> values, IBufferWriter<byte> output) => PlainEncoder.WriteDoubles(values, output);
    }
}

/// <summary>Reusable buffers shared by the column writers of one file (only one column is written at a time).</summary>
internal sealed class WriterBuffers
{
    public ArrayBufferWriter<byte> Values { get; } = new();

    public ArrayBufferWriter<int> Levels { get; } = new();

    public ArrayBufferWriter<bool> Booleans { get; } = new();

    public ArrayBufferWriter<byte> LevelBytes { get; } = new();

    public ArrayBufferWriter<byte> Header { get; } = new();

    public void Reset()
    {
        Values.ResetWrittenCount();
        Levels.ResetWrittenCount();
        Booleans.ResetWrittenCount();
        LevelBytes.ResetWrittenCount();
    }
}
