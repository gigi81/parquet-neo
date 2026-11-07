using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>
/// Reads the values of one column chunk, a batch at a time. Obtain it from <see cref="RowGroupReader.OpenColumn(int)"/>
/// and dispose it when done, which returns its buffers to the pool.
/// </summary>
/// <remarks>
/// <para>
/// A required column fills <c>values</c> and reads <c>values.Length</c> rows. An optional column also takes a
/// <c>definitionLevels</c> span, whose length is the number of rows requested: it receives <c>0</c> for a null and
/// <c>1</c> for a value, and <c>values</c> receives only the non-null values, packed at the start. Because any
/// row may hold a value, <c>values</c> needs room for as many entries as there are rows requested. The result says
/// how many rows and values were read; 0 rows means the chunk is exhausted.
/// </para>
/// <para>Pages are read one at a time, so memory stays bounded by the largest page. Not thread safe.</para>
/// </remarks>
public sealed class ColumnReader : IDisposable
{
    private const int HeaderPrefetch = 8 * 1024;
    private const int MaxHeaderSize = 4 * 1024 * 1024;

    private readonly ParquetFileReader _file;
    private readonly ColumnMetaData _meta;
    private readonly ICodec _codec;
    private readonly int _maxDefinitionLevel;
    private readonly int _levelBitWidth;
    private readonly long _chunkEnd;

    private long _position; // where the next page header starts
    private long _rowsLoaded; // rows in the pages loaded so far
    private long _rowsRead;

    private byte[]? _headerBuffer;
    private byte[]? _compressed;
    private byte[]? _page;
    private int[]? _levels;
    private bool[]? _booleans;

    // The chunk's dictionary (decoded once) and, for a dictionary-encoded page, its decoded indexes.
    private Array? _dictionaryValues; // int[], long[], float[] or double[]
    private byte[]? _dictionaryBytes; // BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY entries
    private ByteRange[]? _dictionaryRanges; // BYTE_ARRAY entries within _dictionaryBytes
    private int _dictionaryCount;
    private bool _hasDictionary;
    private int[]? _indices;
    private bool _pageIsDictionary;

    private int _pageRows;
    private int _pageRowIndex;
    private int _pageValueIndex;
    private int _valuePosition;
    private int _valueEnd;
    private bool _disposed;

    internal ColumnReader(ParquetFileReader file, PrimitiveNode column, ColumnChunk chunk)
    {
        _file = file;
        Column = column;
        string name = string.Join('.', column.Path);

        if (!ReferenceEquals(column.Parent, file.Schema.Root) || column.Repetition == FieldRepetitionType.Repeated)
        {
            throw new NotSupportedException($"Column '{name}' is nested or repeated; only flat columns can be read so far.");
        }

        if (column.Type == PhysicalType.Int96)
        {
            throw new NotSupportedException($"Column '{name}' is INT96, a deprecated type that cannot be read yet.");
        }

        _meta = chunk.MetaData ?? throw new NotSupportedException($"Column '{name}' has no metadata in this file (its data may live in another file).");
        if (chunk.FilePath is not null)
        {
            throw new NotSupportedException($"Column '{name}' is stored in another file ('{chunk.FilePath}'), which is not supported.");
        }

        if (_meta.Type != column.Type)
        {
            throw new ParquetFormatException($"Column '{name}' is {column.Type} in the schema but {_meta.Type} in its chunk.");
        }

        _codec = file.Options.Codecs.Get(_meta.Codec);
        _maxDefinitionLevel = column.MaxDefinitionLevel;
        _levelBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxDefinitionLevel);

        // Dictionary pages come first in a chunk when the column uses them.
        long start = _meta.DictionaryPageOffset is > 0 ? _meta.DictionaryPageOffset.Value : _meta.DataPageOffset;
        if (start < 4 || _meta.TotalCompressedSize < 0 || _meta.NumValues < 0
            || start > file.FileLength || _meta.TotalCompressedSize > file.FileLength - start)
        {
            throw new ParquetFormatException($"Column '{name}' claims bytes {start}..{start + _meta.TotalCompressedSize} in a file of {file.FileLength} bytes.");
        }

        _position = start;
        _chunkEnd = start + _meta.TotalCompressedSize;
    }

    public PrimitiveNode Column { get; }

    /// <summary>The number of rows in this column chunk, as recorded in its metadata.</summary>
    public long RowCount => _meta.NumValues;

    /// <summary>The number of rows read so far.</summary>
    public long RowsRead => _rowsRead;

    public BatchRead ReadBatch(Span<int> values, Span<int> definitionLevels = default) =>
        ReadFixedWidth<int, Int32Decoder>(PhysicalType.Int32, values, definitionLevels);

    public BatchRead ReadBatch(Span<long> values, Span<int> definitionLevels = default) =>
        ReadFixedWidth<long, Int64Decoder>(PhysicalType.Int64, values, definitionLevels);

    public BatchRead ReadBatch(Span<float> values, Span<int> definitionLevels = default) =>
        ReadFixedWidth<float, SingleDecoder>(PhysicalType.Float, values, definitionLevels);

    public BatchRead ReadBatch(Span<double> values, Span<int> definitionLevels = default) =>
        ReadFixedWidth<double, DoubleDecoder>(PhysicalType.Double, values, definitionLevels);

    public BatchRead ReadBatch(Span<bool> values, Span<int> definitionLevels = default)
    {
        int requested = PrepareBatch(PhysicalType.Boolean, values.Length, definitionLevels);
        int rows = 0;
        int valuesRead = 0;
        while (rows < requested)
        {
            if (_pageRowIndex == _pageRows && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - rows, _pageRows - _pageRowIndex);
            int present = TakeLevels(definitionLevels, rows, take);
            _booleans.AsSpan(_pageValueIndex, present).CopyTo(values.Slice(valuesRead, present));
            Advance(take, present);
            rows += take;
            valuesRead += present;
        }

        _rowsRead += rows;
        return new BatchRead(rows, valuesRead);
    }

    /// <summary>
    /// Reads BYTE_ARRAY values as views into the current page's buffer. They are valid until the next call that
    /// reads from this reader, or until it is disposed; copy anything you need to keep.
    /// </summary>
    /// <remarks>
    /// Because the views point into the page, a batch never spans two pages: it ends at the end of the current
    /// page and may return fewer rows than requested even though more remain. Call again until it returns 0 rows.
    /// </remarks>
    public BatchRead ReadByteArrays(Span<ReadOnlyMemory<byte>> values, Span<int> definitionLevels = default)
    {
        int requested = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels);
        int rows = 0;
        int valuesRead = 0;
        Span<ByteRange> ranges = stackalloc ByteRange[256];
        while (rows < requested)
        {
            if (_pageRowIndex == _pageRows)
            {
                // Loading the next page would reuse the buffer the values returned so far point into.
                if (rows > 0 || !LoadNextPage())
                {
                    break;
                }
            }

            int take = Math.Min(requested - rows, _pageRows - _pageRowIndex);
            int present = TakeLevels(definitionLevels, rows, take);
            if (_pageIsDictionary)
            {
                // The entries live in the dictionary, which outlives the page.
                for (int i = 0; i < present; i++)
                {
                    var range = _dictionaryRanges![_indices![_pageValueIndex + i]];
                    values[valuesRead + i] = _dictionaryBytes.AsMemory(range.Offset, range.Length);
                }
            }
            else
            {
                for (int done = 0; done < present;)
                {
                    int count = Math.Min(ranges.Length, present - done);
                    var source = _page.AsSpan(_valuePosition, _valueEnd - _valuePosition);
                    int consumed = PlainDecoder.ReadByteArrays(source, ranges[..count]);
                    for (int i = 0; i < count; i++)
                    {
                        values[valuesRead + done + i] = _page.AsMemory(_valuePosition + ranges[i].Offset, ranges[i].Length);
                    }

                    _valuePosition += consumed;
                    done += count;
                }
            }

            Advance(take, present);
            rows += take;
            valuesRead += present;
        }

        _rowsRead += rows;
        return new BatchRead(rows, valuesRead);
    }

    /// <summary>Reads FIXED_LEN_BYTE_ARRAY values, copied back to back; <paramref name="values"/> holds whole entries of the column's type length.</summary>
    public BatchRead ReadFixedLengthByteArrays(Span<byte> values, Span<int> definitionLevels = default)
    {
        int length = Column.TypeLength ?? 0;
        if (length == 0 || values.Length % length != 0)
        {
            throw new ArgumentException($"The destination must be a whole number of {length}-byte entries.", nameof(values));
        }

        int requested = PrepareBatch(PhysicalType.FixedLenByteArray, values.Length / length, definitionLevels);
        int rows = 0;
        int valuesRead = 0;
        while (rows < requested)
        {
            if (_pageRowIndex == _pageRows && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - rows, _pageRows - _pageRowIndex);
            int present = TakeLevels(definitionLevels, rows, take);
            if (_pageIsDictionary)
            {
                for (int i = 0; i < present; i++)
                {
                    _dictionaryBytes.AsSpan(_indices![_pageValueIndex + i] * length, length).CopyTo(values.Slice((valuesRead + i) * length));
                }
            }
            else
            {
                var source = _page.AsSpan(_valuePosition, _valueEnd - _valuePosition);
                PlainDecoder.ReadFixedLengthByteArrays(source, present, length).CopyTo(values.Slice(valuesRead * length));
                _valuePosition += present * length;
            }

            Advance(take, present);
            rows += take;
            valuesRead += present;
        }

        _rowsRead += rows;
        return new BatchRead(rows, valuesRead);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Return(ref _headerBuffer);
        Return(ref _compressed);
        Return(ref _page);
        if (_levels is not null)
        {
            ArrayPool<int>.Shared.Return(_levels);
            _levels = null;
        }

        if (_booleans is not null)
        {
            ArrayPool<bool>.Shared.Return(_booleans);
            _booleans = null;
        }

        Return(ref _dictionaryBytes);
        if (_dictionaryRanges is not null)
        {
            ArrayPool<ByteRange>.Shared.Return(_dictionaryRanges);
            _dictionaryRanges = null;
        }

        if (_indices is not null)
        {
            ArrayPool<int>.Shared.Return(_indices);
            _indices = null;
        }

        _dictionaryValues = null;
    }

    private BatchRead ReadFixedWidth<T, TDecoder>(PhysicalType expected, Span<T> values, Span<int> definitionLevels)
        where TDecoder : IPlainValueDecoder<T>
    {
        int requested = PrepareBatch(expected, values.Length, definitionLevels);
        int rows = 0;
        int valuesRead = 0;
        while (rows < requested)
        {
            if (_pageRowIndex == _pageRows && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - rows, _pageRows - _pageRowIndex);
            int present = TakeLevels(definitionLevels, rows, take);
            if (_pageIsDictionary)
            {
                var dictionary = (T[])_dictionaryValues!;
                var indices = _indices.AsSpan(_pageValueIndex, present);
                var destination = values.Slice(valuesRead, present);
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = dictionary[indices[i]];
                }
            }
            else
            {
                var source = _page.AsSpan(_valuePosition, _valueEnd - _valuePosition);
                _valuePosition += TDecoder.Read(source, values.Slice(valuesRead, present));
            }

            Advance(take, present);
            rows += take;
            valuesRead += present;
        }

        _rowsRead += rows;
        return new BatchRead(rows, valuesRead);
    }

    /// <summary>Checks a batch request and returns the number of rows it asks for.</summary>
    private int PrepareBatch(PhysicalType expected, int valueCapacity, Span<int> definitionLevels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _file.EnsureOpen();
        if (Column.Type != expected)
        {
            throw new ArgumentException($"Column '{Column.Name}' is {Column.Type}, so it cannot be read as {expected}.");
        }

        if (_maxDefinitionLevel == 0)
        {
            if (!definitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is required, so it has no definition levels.", nameof(definitionLevels));
            }

            return valueCapacity;
        }

        if (definitionLevels.IsEmpty)
        {
            if (valueCapacity != 0)
            {
                throw new ArgumentException($"Column '{Column.Name}' is optional, so a span for its definition levels is needed.", nameof(definitionLevels));
            }

            return 0;
        }

        if (valueCapacity < definitionLevels.Length)
        {
            throw new ArgumentException("The values span needs room for one value per requested row, since any row may hold a value.");
        }

        return definitionLevels.Length;
    }

    /// <summary>Copies the levels of the next <paramref name="take"/> rows of the page and returns how many hold a value.</summary>
    private int TakeLevels(Span<int> definitionLevels, int row, int take)
    {
        if (_maxDefinitionLevel == 0)
        {
            return take;
        }

        var slice = _levels.AsSpan(_pageRowIndex, take);
        slice.CopyTo(definitionLevels.Slice(row, take));
        return slice.Count(_maxDefinitionLevel);
    }

    private void Advance(int rows, int values)
    {
        _pageRowIndex += rows;
        _pageValueIndex += values;
    }

    private bool LoadNextPage()
    {
        _pageRows = _pageRowIndex = _pageValueIndex = 0;
        while (_position < _chunkEnd)
        {
            var header = ReadPageHeader(out int headerLength);
            long dataStart = _position + headerLength;
            int compressedSize = header.CompressedPageSize;
            int uncompressedSize = header.UncompressedPageSize;
            if (compressedSize < 0 || uncompressedSize < 0 || compressedSize > _chunkEnd - dataStart)
            {
                throw Corrupt($"a page of {compressedSize} compressed bytes does not fit in the column chunk");
            }

            if (uncompressedSize > _file.Options.MaxPageSize)
            {
                throw Corrupt($"a page of {uncompressedSize} bytes is over the limit of {_file.Options.MaxPageSize}");
            }

            _position = dataStart + compressedSize;
            switch (header.Type)
            {
                case PageType.DictionaryPage:
                    ReadDictionaryPage(header, dataStart, compressedSize, uncompressedSize);
                    continue;
                case PageType.DataPage:
                    ReadDataPageV1(header, dataStart, compressedSize, uncompressedSize);
                    break;
                case PageType.DataPageV2:
                    ReadDataPageV2(header, dataStart, compressedSize, uncompressedSize);
                    break;
                default:
                    continue; // index pages and page types from the future are skipped
            }

            if (_pageRows > 0)
            {
                return true;
            }
        }

        if (_rowsLoaded != _meta.NumValues)
        {
            throw Corrupt($"its pages hold {_rowsLoaded} values but the column chunk declares {_meta.NumValues}");
        }

        return false;
    }

    private PageHeader ReadPageHeader(out int headerLength)
    {
        int size = (int)Math.Min(HeaderPrefetch, _chunkEnd - _position);
        while (true)
        {
            EnsureCapacity(ref _headerBuffer, size);
            _file.ReadAt(_position, _headerBuffer.AsSpan(0, size));
            try
            {
                return PageHeader.Deserialize(_headerBuffer.AsSpan(0, size), out headerLength);
            }
            catch (ParquetFormatException) when (size < _chunkEnd - _position && size < MaxHeaderSize)
            {
                // The header may simply be longer than what was read (large statistics); read more and retry.
                size = (int)Math.Min(Math.Min((long)size * 4, _chunkEnd - _position), MaxHeaderSize);
            }
        }
    }

    /// <summary>Loads the chunk's dictionary: decoded once, then shared by every dictionary-encoded page.</summary>
    private void ReadDictionaryPage(PageHeader header, long dataStart, int compressedSize, int uncompressedSize)
    {
        var info = header.DictionaryPageHeader ?? throw Corrupt("a dictionary page has no dictionary page header");
        if (_hasDictionary)
        {
            throw Corrupt("it has more than one dictionary page");
        }

        if (_rowsLoaded > 0)
        {
            throw Corrupt("its dictionary page comes after a data page");
        }

        if (info.Encoding is not (ParquetEncoding.Plain or ParquetEncoding.PlainDictionary))
        {
            throw new NotSupportedException($"Column '{Column.Name}' has a dictionary page encoded as {info.Encoding}; only PLAIN is supported.");
        }

        int count = CheckRows(info.NumValues);
        LoadPageV1(dataStart, compressedSize, uncompressedSize);
        var source = _page.AsSpan(0, uncompressedSize);

        // Each size is checked against the bytes present before anything is allocated for it.
        switch (Column.Type)
        {
            case PhysicalType.Int32:
                _dictionaryValues = ReadDictionary<int>(source, count, sizeof(int), PlainDecoder.ReadInt32s);
                break;
            case PhysicalType.Int64:
                _dictionaryValues = ReadDictionary<long>(source, count, sizeof(long), PlainDecoder.ReadInt64s);
                break;
            case PhysicalType.Float:
                _dictionaryValues = ReadDictionary<float>(source, count, sizeof(float), PlainDecoder.ReadSingles);
                break;
            case PhysicalType.Double:
                _dictionaryValues = ReadDictionary<double>(source, count, sizeof(double), PlainDecoder.ReadDoubles);
                break;
            case PhysicalType.ByteArray:
                if (count > uncompressedSize / sizeof(int))
                {
                    throw Corrupt($"a dictionary of {count} byte arrays does not fit in {uncompressedSize} bytes");
                }

                _dictionaryRanges = ArrayPool<ByteRange>.Shared.Rent(Math.Max(count, 1));
                PlainDecoder.ReadByteArrays(source, _dictionaryRanges.AsSpan(0, count));
                CopyDictionaryBytes(source);
                break;
            case PhysicalType.FixedLenByteArray:
                int length = Column.TypeLength ?? 0;
                if (length <= 0 || (long)count * length > uncompressedSize)
                {
                    throw Corrupt($"a dictionary of {count} entries of {length} bytes does not fit in {uncompressedSize} bytes");
                }

                CopyDictionaryBytes(source[..(count * length)]);
                break;
            default:
                throw Corrupt($"a {Column.Type} column cannot have a dictionary");
        }

        _dictionaryCount = count;
        _hasDictionary = true;
    }

    private T[] ReadDictionary<T>(ReadOnlySpan<byte> source, int count, int width, Func<ReadOnlySpan<byte>, Span<T>, int> read)
    {
        if ((long)count * width > source.Length)
        {
            throw Corrupt($"a dictionary of {count} entries does not fit in {source.Length} bytes");
        }

        var values = new T[count];
        read(source, values);
        return values;
    }

    private void CopyDictionaryBytes(ReadOnlySpan<byte> source)
    {
        EnsureCapacity(ref _dictionaryBytes, source.Length);
        source.CopyTo(_dictionaryBytes);
    }

    /// <summary>Decodes the dictionary indexes of a page: a bit-width byte followed by RLE/bit-packed data.</summary>
    private void DecodeIndices(int start, int end, int count)
    {
        if (!_hasDictionary)
        {
            throw Corrupt("a data page uses a dictionary but the column chunk has no dictionary page");
        }

        EnsureCapacity(ref _indices, count);
        if (count == 0)
        {
            return;
        }

        if (end <= start)
        {
            throw Corrupt("a dictionary-encoded page has no index data");
        }

        int bitWidth = _page![start];
        if (bitWidth > 32)
        {
            throw Corrupt($"a dictionary index bit width of {bitWidth} is above 32");
        }

        var indices = _indices.AsSpan(0, count);
        var decoder = new RleBitPackedDecoder(_page.AsSpan(start + 1, end - start - 1), bitWidth);
        if (decoder.ReadValues(indices) != count)
        {
            throw Corrupt("a page has fewer dictionary indexes than values");
        }

        if (_dictionaryCount == 0 || indices.IndexOfAnyExceptInRange(0, _dictionaryCount - 1) >= 0)
        {
            throw Corrupt($"a dictionary index is outside the dictionary of {_dictionaryCount} entries");
        }
    }

    private void ReadDataPageV1(PageHeader header, long dataStart, int compressedSize, int uncompressedSize)
    {
        var info = header.DataPageHeader ?? throw Corrupt("a data page has no data page header");
        int rows = CheckRows(info.NumValues);
        CheckEncoding(info.Encoding);
        LoadPageV1(dataStart, compressedSize, uncompressedSize);

        int position = 0;
        if (_maxDefinitionLevel > 0)
        {
            if (info.DefinitionLevelEncoding != ParquetEncoding.Rle)
            {
                throw new NotSupportedException($"Definition levels encoded as {info.DefinitionLevelEncoding} are not supported (only RLE).");
            }

            if (uncompressedSize < sizeof(int))
            {
                throw Corrupt("a data page is too short for its definition levels");
            }

            int levelBytes = BinaryPrimitives.ReadInt32LittleEndian(_page);
            if (levelBytes < 0 || levelBytes > uncompressedSize - sizeof(int))
            {
                throw Corrupt("the definition levels of a page are longer than the page");
            }

            DecodeLevels(_page.AsSpan(sizeof(int), levelBytes), rows);
            position = sizeof(int) + levelBytes;
        }

        StartPage(position, uncompressedSize, rows, info.Encoding);
    }

    private void ReadDataPageV2(PageHeader header, long dataStart, int compressedSize, int uncompressedSize)
    {
        var info = header.DataPageHeaderV2 ?? throw Corrupt("a data page v2 has no data page header");
        int rows = CheckRows(info.NumValues);
        CheckEncoding(info.Encoding);
        int definitionBytes = info.DefinitionLevelsByteLength;
        if (info.RepetitionLevelsByteLength != 0)
        {
            throw Corrupt("a flat column has repetition levels");
        }

        if (definitionBytes < 0 || definitionBytes > Math.Min(compressedSize, uncompressedSize) || (_maxDefinitionLevel == 0 && definitionBytes != 0))
        {
            throw Corrupt("the definition level length of a page does not fit");
        }

        // v2 stores the levels uncompressed, followed by the values, which are compressed on their own.
        EnsureCapacity(ref _compressed, compressedSize);
        _file.ReadAt(dataStart, _compressed.AsSpan(0, compressedSize));
        EnsureCapacity(ref _page, uncompressedSize);
        _compressed.AsSpan(0, definitionBytes).CopyTo(_page);

        var storedValues = _compressed.AsSpan(definitionBytes, compressedSize - definitionBytes);
        var values = _page.AsSpan(definitionBytes, uncompressedSize - definitionBytes);
        if (info.IsCompressed && _codec.Id != CompressionCodec.Uncompressed)
        {
            _codec.Decompress(storedValues, values);
        }
        else if (storedValues.Length == values.Length)
        {
            storedValues.CopyTo(values);
        }
        else
        {
            throw Corrupt("an uncompressed page body does not have the size its header declares");
        }

        if (_maxDefinitionLevel > 0)
        {
            DecodeLevels(_page.AsSpan(0, definitionBytes), rows);
        }

        StartPage(definitionBytes, uncompressedSize, rows, info.Encoding);
    }

    /// <summary>Reads a v1 page body and leaves its uncompressed bytes in <see cref="_page"/>.</summary>
    private void LoadPageV1(long dataStart, int compressedSize, int uncompressedSize)
    {
        EnsureCapacity(ref _compressed, compressedSize);
        _file.ReadAt(dataStart, _compressed.AsSpan(0, compressedSize));
        if (_codec.Id == CompressionCodec.Uncompressed)
        {
            if (compressedSize != uncompressedSize)
            {
                throw Corrupt("an uncompressed page has different compressed and uncompressed sizes");
            }

            (_page, _compressed) = (_compressed, _page); // no copy: the bytes read are the page
            return;
        }

        EnsureCapacity(ref _page, uncompressedSize);
        _codec.Decompress(_compressed.AsSpan(0, compressedSize), _page.AsSpan(0, uncompressedSize));
    }

    private void DecodeLevels(ReadOnlySpan<byte> encoded, int rows)
    {
        EnsureCapacity(ref _levels, rows);
        var levels = _levels.AsSpan(0, rows);
        var decoder = new RleBitPackedDecoder(encoded, _levelBitWidth);
        if (decoder.ReadValues(levels) != rows)
        {
            throw Corrupt("a page has fewer definition levels than values");
        }

        // Cannot trigger while only flat columns are read (the maximum is 1 and levels are 1 bit wide), but a
        // nested column's levels are wider than its maximum needs, so this will guard them.
        if (levels.IndexOfAnyExceptInRange(0, _maxDefinitionLevel) >= 0)
        {
            throw Corrupt("a definition level is above the column's maximum");
        }
    }

    /// <summary>Records the shape of the page that was just loaded and prepares its values for reading.</summary>
    private void StartPage(int valuesStart, int valuesEnd, int rows, ParquetEncoding encoding)
    {
        _pageRows = rows;
        _pageRowIndex = 0;
        _pageValueIndex = 0;
        _rowsLoaded += rows;
        _valuePosition = valuesStart;
        _valueEnd = valuesEnd;

        _pageIsDictionary = encoding is ParquetEncoding.PlainDictionary or ParquetEncoding.RleDictionary;
        if (Column.Type == PhysicalType.Boolean || _pageIsDictionary)
        {
            int present = _maxDefinitionLevel == 0 ? rows : _levels.AsSpan(0, rows).Count(_maxDefinitionLevel);
            if (_pageIsDictionary)
            {
                DecodeIndices(valuesStart, valuesEnd, present);
            }
            else
            {
                DecodeBooleans(valuesStart, valuesEnd, present, encoding);
            }
        }
    }

    /// <summary>Booleans are bit-packed per page (PLAIN) or RLE with a length prefix, so a page is unpacked at once.</summary>
    private void DecodeBooleans(int start, int end, int count, ParquetEncoding encoding)
    {
        EnsureCapacity(ref _booleans, count);
        var destination = _booleans.AsSpan(0, count);
        var source = _page.AsSpan(start, end - start);
        if (encoding == ParquetEncoding.Plain)
        {
            PlainDecoder.ReadBooleans(source, destination);
            return;
        }

        if (source.Length < sizeof(int))
        {
            throw Corrupt("an RLE boolean section is too short for its length");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source);
        if (length < 0 || length > source.Length - sizeof(int))
        {
            throw Corrupt("an RLE boolean section is longer than its page");
        }

        var decoder = new RleBitPackedDecoder(source.Slice(sizeof(int), length), 1);
        Span<int> chunk = stackalloc int[256];
        for (int done = 0; done < count;)
        {
            int n = Math.Min(chunk.Length, count - done);
            if (decoder.ReadValues(chunk[..n]) != n)
            {
                throw Corrupt("an RLE boolean section has fewer values than its page");
            }

            for (int i = 0; i < n; i++)
            {
                destination[done + i] = chunk[i] != 0;
            }

            done += n;
        }
    }

    private int CheckRows(int rows)
    {
        if (rows < 0 || rows > _file.Options.MaxRowsPerPage)
        {
            throw Corrupt($"a page declares {rows} values, outside the limit of {_file.Options.MaxRowsPerPage}");
        }

        return rows;
    }

    private void CheckEncoding(ParquetEncoding encoding)
    {
        switch (encoding)
        {
            case ParquetEncoding.Plain:
                return;
            case ParquetEncoding.Rle when Column.Type == PhysicalType.Boolean:
                return;
            case ParquetEncoding.PlainDictionary or ParquetEncoding.RleDictionary:
                if (Column.Type == PhysicalType.Boolean)
                {
                    throw Corrupt("a boolean column cannot be dictionary encoded");
                }

                return;
            default:
                throw new NotSupportedException($"Column '{Column.Name}' uses the {encoding} encoding, which is not supported yet.");
        }
    }

    private ParquetFormatException Corrupt(string detail) => new($"Column '{Column.Name}' is corrupt: {detail}.");

    private static void EnsureCapacity(ref byte[]? buffer, int size)
    {
        if (buffer is null || buffer.Length < size)
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 64));
        }
    }

    private static void EnsureCapacity(ref int[]? buffer, int size)
    {
        if (buffer is null || buffer.Length < size)
        {
            if (buffer is not null)
            {
                ArrayPool<int>.Shared.Return(buffer);
            }

            buffer = ArrayPool<int>.Shared.Rent(Math.Max(size, 64));
        }
    }

    private static void EnsureCapacity(ref bool[]? buffer, int size)
    {
        if (buffer is null || buffer.Length < size)
        {
            if (buffer is not null)
            {
                ArrayPool<bool>.Shared.Return(buffer);
            }

            buffer = ArrayPool<bool>.Shared.Rent(Math.Max(size, 64));
        }
    }

    private static void Return(ref byte[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;
        }
    }

    private interface IPlainValueDecoder<T>
    {
        static abstract int Read(ReadOnlySpan<byte> source, Span<T> destination);
    }

    private readonly struct Int32Decoder : IPlainValueDecoder<int>
    {
        public static int Read(ReadOnlySpan<byte> source, Span<int> destination) => PlainDecoder.ReadInt32s(source, destination);
    }

    private readonly struct Int64Decoder : IPlainValueDecoder<long>
    {
        public static int Read(ReadOnlySpan<byte> source, Span<long> destination) => PlainDecoder.ReadInt64s(source, destination);
    }

    private readonly struct SingleDecoder : IPlainValueDecoder<float>
    {
        public static int Read(ReadOnlySpan<byte> source, Span<float> destination) => PlainDecoder.ReadSingles(source, destination);
    }

    private readonly struct DoubleDecoder : IPlainValueDecoder<double>
    {
        public static int Read(ReadOnlySpan<byte> source, Span<double> destination) => PlainDecoder.ReadDoubles(source, destination);
    }
}
