using System.Buffers.Binary;
using System.Buffers;
using System.Numerics;
using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;
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
/// <c>definitionLevels</c> span, whose length is the number of entries requested: it receives <c>0</c> for a null and
/// <c>1</c> for a value, and <c>values</c> receives only the non-null values, packed at the start. Because any
/// row may hold a value, <c>values</c> needs room for as many entries as there are rows requested. The result says
/// how many rows and values were read; 0 rows means the chunk is exhausted.
/// </para>
/// <para>Pages are read one at a time, so memory stays bounded by the largest page. Not thread safe.</para>
/// </remarks>
public sealed partial class ColumnReader : IDisposable
{
    private const int HeaderPrefetch = 8 * 1024;
    private const int MaxHeaderSize = 4 * 1024 * 1024;

    private readonly ParquetFileReader _file;
    private readonly ColumnMetaData _meta;
    private readonly ICodec _codec;
    private readonly int _maxDefinitionLevel;
    private readonly int _levelBitWidth;
    private readonly int _maxRepetitionLevel;
    private readonly int _repetitionBitWidth;
    private readonly long _rowCount;
    private readonly long _chunkEnd;

    private long _position; // where the next page header starts
    private long _entriesLoaded; // level entries in the pages loaded so far
    private long _recordsLoaded; // of those, the ones that start a row
    private long _entriesRead;

    private byte[]? _headerBuffer;
    private byte[]? _compressed;
    private byte[]? _page;
    private ReadOnlyMemory<byte> _valueSource; // where PLAIN values are read from: the page, or the page converted to PLAIN
    private readonly ArrayBufferWriter<byte> _converted = new();
    private int[]? _levels;
    private int[]? _repetitionLevels;
    private bool[]? _booleans;

    // The chunk's dictionary (decoded once) and, for a dictionary-encoded page, its decoded indexes.
    private Array? _dictionaryValues; // int[], long[], float[] or double[]
    private byte[]? _dictionaryBytes; // BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY entries
    private ByteRange[]? _dictionaryRanges; // BYTE_ARRAY entries within _dictionaryBytes
    private int _dictionaryCount;
    private bool _hasDictionary;
    private int[]? _indices;
    private bool _pageIsDictionary;

    private int _pageEntries;
    private int _pageEntryIndex;
    private int _pageValueIndex;
    private int _valuePosition;
    private int _valueEnd;
    private bool _disposed;

    internal ColumnReader(ParquetFileReader file, PrimitiveNode column, ColumnChunk chunk, long rowCount)
    {
        _file = file;
        Column = column;
        string name = string.Join('.', column.Path);

        _meta = chunk.MetaData ?? throw new NotSupportedException($"Column '{name}' has no metadata in this file (its data may live in another file).");
        if (chunk.FilePath is not null)
        {
            throw new NotSupportedException($"Column '{name}' is stored in another file ('{chunk.FilePath}'), which is not supported.");
        }

        if (_meta.Type != column.Type)
        {
            throw new CorruptColumnException(name, $"it is {column.Type} in the schema but {_meta.Type} in its chunk");
        }

        _codec = file.Options.Codecs.Get(_meta.Codec);
        _maxDefinitionLevel = column.MaxDefinitionLevel;
        _levelBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxDefinitionLevel);
        _maxRepetitionLevel = column.MaxRepetitionLevel;
        _repetitionBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxRepetitionLevel);
        _rowCount = rowCount;

        // Dictionary pages come first in a chunk when the column uses them.
        long start = _meta.DictionaryPageOffset is > 0 ? _meta.DictionaryPageOffset.Value : _meta.DataPageOffset;
        if (start < 4 || _meta.TotalCompressedSize < 0 || _meta.NumValues < 0
            || start > file.FileLength || _meta.TotalCompressedSize > file.FileLength - start)
        {
            throw new CorruptColumnException(name, $"it claims bytes {start}..{start + _meta.TotalCompressedSize} in a file of {file.FileLength} bytes");
        }

        _position = start;
        _chunkEnd = start + _meta.TotalCompressedSize;
    }

    public PrimitiveNode Column { get; }

    /// <summary>
    /// The number of level entries in this column chunk, as recorded in its metadata: one per value, null, empty list and
    /// so on. For a column that is not repeated this is the number of rows.
    /// </summary>
    public long EntryCount => _meta.NumValues;

    /// <summary>The number of level entries read so far.</summary>
    public long EntriesRead => _entriesRead;

    public BatchRead ReadBatch(Span<int> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadFixedWidth<int, Int32Decoder>(PhysicalType.Int32, values, definitionLevels, repetitionLevels);

    public BatchRead ReadBatch(Span<long> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadFixedWidth<long, Int64Decoder>(PhysicalType.Int64, values, definitionLevels, repetitionLevels);

    public BatchRead ReadBatch(Span<float> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadFixedWidth<float, SingleDecoder>(PhysicalType.Float, values, definitionLevels, repetitionLevels);

    public BatchRead ReadBatch(Span<double> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadFixedWidth<double, DoubleDecoder>(PhysicalType.Double, values, definitionLevels, repetitionLevels);

    public BatchRead ReadBatch(Span<bool> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        int requested = PrepareBatch(PhysicalType.Boolean, values.Length, definitionLevels, repetitionLevels);
        int entries = 0;
        int valuesRead = 0;
        while (entries < requested)
        {
            if (_pageEntryIndex == _pageEntries && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - entries, _pageEntries - _pageEntryIndex);
            int present = TakeLevels(definitionLevels, repetitionLevels, entries, take);
            _booleans.AsSpan(_pageValueIndex, present).CopyTo(values.Slice(valuesRead, present));
            Advance(take, present);
            entries += take;
            valuesRead += present;
        }

        _entriesRead += entries;
        return new BatchRead(entries, valuesRead);
    }

    /// <summary>
    /// Reads BYTE_ARRAY values as views into the current page's buffer. They are valid until the next call that
    /// reads from this reader, or until it is disposed; copy anything you need to keep.
    /// </summary>
    /// <remarks>
    /// Because the views point into the page, a batch never spans two pages: it ends at the end of the current
    /// page and may return fewer rows than requested even though more remain. Call again until it returns 0 rows.
    /// </remarks>
    public BatchRead ReadByteArrays(Span<ReadOnlyMemory<byte>> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        int requested = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels, repetitionLevels);
        int entries = 0;
        int valuesRead = 0;
        Span<ByteRange> ranges = stackalloc ByteRange[256];
        while (entries < requested)
        {
            // Loading the next page would reuse the buffer the values returned so far point into.
            if (_pageEntryIndex == _pageEntries && (entries > 0 || !LoadNextPage()))
            {
                break;
            }

            int take = Math.Min(requested - entries, _pageEntries - _pageEntryIndex);
            int present = TakeLevels(definitionLevels, repetitionLevels, entries, take);
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
                    var source = _valueSource.Span.Slice(_valuePosition, _valueEnd - _valuePosition);
                    int consumed = PlainDecoder.ReadByteArrays(source, ranges[..count]);
                    for (int i = 0; i < count; i++)
                    {
                        values[valuesRead + done + i] = _valueSource.Slice(_valuePosition + ranges[i].Offset, ranges[i].Length);
                    }

                    _valuePosition += consumed;
                    done += count;
                }
            }

            Advance(take, present);
            entries += take;
            valuesRead += present;
        }

        _entriesRead += entries;
        return new BatchRead(entries, valuesRead);
    }

    /// <summary>Reads FIXED_LEN_BYTE_ARRAY values, copied back to back; <paramref name="values"/> holds whole entries of the column's type length.</summary>
    public BatchRead ReadFixedLengthByteArrays(Span<byte> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        int length = Column.TypeLength ?? 0;
        if (length == 0 || values.Length % length != 0)
        {
            throw new ArgumentException($"The destination must be a whole number of {length}-byte entries.", nameof(values));
        }

        int requested = PrepareBatch(PhysicalType.FixedLenByteArray, values.Length / length, definitionLevels, repetitionLevels);
        int entries = 0;
        int valuesRead = 0;
        while (entries < requested)
        {
            if (_pageEntryIndex == _pageEntries && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - entries, _pageEntries - _pageEntryIndex);
            int present = TakeLevels(definitionLevels, repetitionLevels, entries, take);
            if (_pageIsDictionary)
            {
                for (int i = 0; i < present; i++)
                {
                    _dictionaryBytes.AsSpan(_indices![_pageValueIndex + i] * length, length).CopyTo(values.Slice((valuesRead + i) * length));
                }
            }
            else
            {
                var source = _valueSource.Span.Slice(_valuePosition, _valueEnd - _valuePosition);
                PlainDecoder.ReadFixedLengthByteArrays(source, present, length).CopyTo(values.Slice(valuesRead * length));
                _valuePosition += present * length;
            }

            Advance(take, present);
            entries += take;
            valuesRead += present;
        }

        _entriesRead += entries;
        return new BatchRead(entries, valuesRead);
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

        if (_repetitionLevels is not null)
        {
            ArrayPool<int>.Shared.Return(_repetitionLevels);
            _repetitionLevels = null;
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

    private BatchRead ReadFixedWidth<T, TDecoder>(PhysicalType expected, Span<T> values, Span<int> definitionLevels, Span<int> repetitionLevels)
        where TDecoder : IPlainValueDecoder<T>
    {
        int requested = PrepareBatch(expected, values.Length, definitionLevels, repetitionLevels);
        int entries = 0;
        int valuesRead = 0;
        while (entries < requested)
        {
            if (_pageEntryIndex == _pageEntries && !LoadNextPage())
            {
                break;
            }

            int take = Math.Min(requested - entries, _pageEntries - _pageEntryIndex);
            int present = TakeLevels(definitionLevels, repetitionLevels, entries, take);
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
                var source = _valueSource.Span.Slice(_valuePosition, _valueEnd - _valuePosition);
                _valuePosition += TDecoder.Read(source, values.Slice(valuesRead, present));
            }

            Advance(take, present);
            entries += take;
            valuesRead += present;
        }

        _entriesRead += entries;
        return new BatchRead(entries, valuesRead);
    }

    /// <summary>Checks a batch request and returns the number of level entries it asks for.</summary>
    private int PrepareBatch(PhysicalType expected, int valueCapacity, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _file.EnsureOpen();
        if (Column.Type != expected)
        {
            throw new ArgumentException($"Column '{Column.Name}' is {Column.Type}, so it cannot be read as {expected}.");
        }

        int entries;
        if (_maxDefinitionLevel == 0)
        {
            if (!definitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is required, so it has no definition levels.", nameof(definitionLevels));
            }

            entries = valueCapacity;
        }
        else if (definitionLevels.IsEmpty)
        {
            if (valueCapacity != 0)
            {
                throw new ArgumentException($"Column '{Column.Name}' is optional, so a span for its definition levels is needed.", nameof(definitionLevels));
            }

            entries = 0;
        }
        else
        {
            if (valueCapacity < definitionLevels.Length)
            {
                throw new ArgumentException("The values span needs room for one value per requested entry, since any entry may hold a value.");
            }

            entries = definitionLevels.Length;
        }

        if (_maxRepetitionLevel == 0)
        {
            if (!repetitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is not repeated, so it has no repetition levels.", nameof(repetitionLevels));
            }
        }
        else if (repetitionLevels.Length != entries)
        {
            throw new ArgumentException($"Column '{Column.Name}' is repeated, so a span for its repetition levels, as long as the one for the definition levels, is needed.", nameof(repetitionLevels));
        }

        return entries;
    }

    /// <summary>Copies the levels of the next <paramref name="take"/> entries of the page and returns how many hold a value.</summary>
    private int TakeLevels(Span<int> definitionLevels, Span<int> repetitionLevels, int entry, int take)
    {
        if (_maxRepetitionLevel > 0)
        {
            _repetitionLevels.AsSpan(_pageEntryIndex, take).CopyTo(repetitionLevels.Slice(entry, take));
        }

        if (_maxDefinitionLevel == 0)
        {
            return take;
        }

        var slice = _levels.AsSpan(_pageEntryIndex, take);
        slice.CopyTo(definitionLevels.Slice(entry, take));
        return slice.Count(_maxDefinitionLevel);
    }

    private void Advance(int entries, int values)
    {
        _pageEntryIndex += entries;
        _pageValueIndex += values;
    }

    private bool LoadNextPage()
    {
        _pageEntries = _pageEntryIndex = _pageValueIndex = 0;
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

            if (_pageEntries > 0)
            {
                return true;
            }
        }

        if (_entriesLoaded != _meta.NumValues)
        {
            throw Corrupt($"its pages hold {_entriesLoaded} values but the column chunk declares {_meta.NumValues}");
        }

        if (_recordsLoaded != _rowCount)
        {
            throw Corrupt($"its pages hold {_recordsLoaded} rows but the row group declares {_rowCount}");
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
            catch (MetadataException) when (size < _chunkEnd - _position && size < MaxHeaderSize)
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

        if (_entriesLoaded > 0)
        {
            throw Corrupt("its dictionary page comes after a data page");
        }

        if (info.Encoding is not (ParquetEncoding.Plain or ParquetEncoding.PlainDictionary))
        {
            throw new NotSupportedException($"Column '{Column.Name}' has a dictionary page encoded as {info.Encoding}; only PLAIN is supported.");
        }

        int count = CheckEntryCount(info.NumValues);
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
            case PhysicalType.Int96:
                _dictionaryValues = ReadDictionary<Int96>(source, count, 12, PlainDecoder.ReadInt96s);
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
        int entries = CheckEntryCount(info.NumValues);
        CheckEncoding(info.Encoding);
        LoadPageV1(dataStart, compressedSize, uncompressedSize);

        // [repetition levels][definition levels][values]: each level section is a 4-byte length and RLE data.
        int position = 0;
        if (_maxRepetitionLevel > 0)
        {
            if (info.RepetitionLevelEncoding != ParquetEncoding.Rle)
            {
                throw new NotSupportedException($"Repetition levels encoded as {info.RepetitionLevelEncoding} are not supported (only RLE).");
            }

            DecodeRepetitionLevels(ReadLevelSection(ref position, uncompressedSize, "repetition"), entries);
        }

        if (_maxDefinitionLevel > 0)
        {
            if (info.DefinitionLevelEncoding != ParquetEncoding.Rle)
            {
                throw new NotSupportedException($"Definition levels encoded as {info.DefinitionLevelEncoding} are not supported (only RLE).");
            }

            DecodeLevels(ReadLevelSection(ref position, uncompressedSize, "definition"), entries);
        }

        StartPage(position, uncompressedSize, entries, info.Encoding);
    }

    /// <summary>Returns the RLE data of the level section that starts at <paramref name="position"/> and moves past it.</summary>
    private ReadOnlySpan<byte> ReadLevelSection(ref int position, int pageSize, string what)
    {
        if (pageSize - position < sizeof(int))
        {
            throw Corrupt($"a data page is too short for its {what} levels");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(_page.AsSpan(position));
        if (length < 0 || length > pageSize - position - sizeof(int))
        {
            throw Corrupt($"the {what} levels of a page are longer than the page");
        }

        var section = _page.AsSpan(position + sizeof(int), length);
        position += sizeof(int) + length;
        return section;
    }

    private void ReadDataPageV2(PageHeader header, long dataStart, int compressedSize, int uncompressedSize)
    {
        var info = header.DataPageHeaderV2 ?? throw Corrupt("a data page v2 has no data page header");
        int entries = CheckEntryCount(info.NumValues);
        CheckEncoding(info.Encoding);
        int repetitionBytes = info.RepetitionLevelsByteLength;
        int definitionBytes = info.DefinitionLevelsByteLength;
        long levelBytes = (long)repetitionBytes + definitionBytes;
        if (repetitionBytes < 0 || definitionBytes < 0 || levelBytes > Math.Min(compressedSize, uncompressedSize)
            || (_maxDefinitionLevel == 0 && definitionBytes != 0) || (_maxRepetitionLevel == 0 && repetitionBytes != 0))
        {
            throw Corrupt("the level lengths of a page do not fit");
        }

        // v2 stores the levels uncompressed, followed by the values, which are compressed on their own.
        int levelsLength = (int)levelBytes;
        EnsureCapacity(ref _compressed, compressedSize);
        _file.ReadAt(dataStart, _compressed.AsSpan(0, compressedSize));
        EnsureCapacity(ref _page, uncompressedSize);
        _compressed.AsSpan(0, levelsLength).CopyTo(_page);

        var storedValues = _compressed.AsSpan(levelsLength, compressedSize - levelsLength);
        var values = _page.AsSpan(levelsLength, uncompressedSize - levelsLength);
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

        if (_maxRepetitionLevel > 0)
        {
            DecodeRepetitionLevels(_page.AsSpan(0, repetitionBytes), entries);
        }

        if (_maxDefinitionLevel > 0)
        {
            DecodeLevels(_page.AsSpan(repetitionBytes, definitionBytes), entries);
        }

        StartPage(levelsLength, uncompressedSize, entries, info.Encoding);
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

    private void DecodeLevels(ReadOnlySpan<byte> encoded, int entries)
    {
        EnsureCapacity(ref _levels, entries);
        var levels = _levels.AsSpan(0, entries);
        var decoder = new RleBitPackedDecoder(encoded, _levelBitWidth);
        if (decoder.ReadValues(levels) != entries)
        {
            throw Corrupt("a page has fewer definition levels than values");
        }

        // The bit width has room for values above the maximum (a maximum of 2 takes 2 bits, which hold a 3).
        if (levels.IndexOfAnyExceptInRange(0, _maxDefinitionLevel) >= 0)
        {
            throw Corrupt("a definition level is above the column's maximum");
        }
    }

    private void DecodeRepetitionLevels(ReadOnlySpan<byte> encoded, int entries)
    {
        EnsureCapacity(ref _repetitionLevels, entries);
        var levels = _repetitionLevels.AsSpan(0, entries);
        var decoder = new RleBitPackedDecoder(encoded, _repetitionBitWidth);
        if (decoder.ReadValues(levels) != entries)
        {
            throw Corrupt("a page has fewer repetition levels than values");
        }

        if (levels.IndexOfAnyExceptInRange(0, _maxRepetitionLevel) >= 0)
        {
            throw Corrupt("a repetition level is above the column's maximum");
        }

        if (entries > 0 && _entriesLoaded == 0 && levels[0] != 0)
        {
            throw Corrupt("the column chunk does not start with a new row");
        }
    }

    /// <summary>Records the shape of the page that was just loaded and prepares its values for reading.</summary>
    private void StartPage(int valuesStart, int valuesEnd, int entries, ParquetEncoding encoding)
    {
        _pageEntries = entries;
        _pageEntryIndex = 0;
        _pageValueIndex = 0;
        _entriesLoaded += entries;
        _recordsLoaded += _maxRepetitionLevel == 0 ? entries : _repetitionLevels.AsSpan(0, entries).Count(0);
        _valuePosition = valuesStart;
        _valueEnd = valuesEnd;
        _valueSource = _page;

        if (encoding is ParquetEncoding.DeltaBinaryPacked or ParquetEncoding.DeltaLengthByteArray or ParquetEncoding.DeltaByteArray or ParquetEncoding.ByteStreamSplit)
        {
            int present = _maxDefinitionLevel == 0 ? entries : _levels.AsSpan(0, entries).Count(_maxDefinitionLevel);
            ConvertToPlain(encoding, _page.AsSpan(valuesStart, valuesEnd - valuesStart), present);
        }

        _pageIsDictionary = encoding is ParquetEncoding.PlainDictionary or ParquetEncoding.RleDictionary;
        if (Column.Type == PhysicalType.Boolean || _pageIsDictionary)
        {
            int present = _maxDefinitionLevel == 0 ? entries : _levels.AsSpan(0, entries).Count(_maxDefinitionLevel);
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

    private int CheckEntryCount(int entries)
    {
        if (entries < 0 || entries > _file.Options.MaxRowsPerPage)
        {
            throw Corrupt($"a page declares {entries} values, outside the limit of {_file.Options.MaxRowsPerPage}");
        }

        return entries;
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
            case ParquetEncoding.DeltaBinaryPacked:
                RequireTypes(encoding, PhysicalType.Int32, PhysicalType.Int64);
                return;
            case ParquetEncoding.DeltaLengthByteArray:
                RequireTypes(encoding, PhysicalType.ByteArray);
                return;
            case ParquetEncoding.DeltaByteArray:
                RequireTypes(encoding, PhysicalType.ByteArray, PhysicalType.FixedLenByteArray);
                return;
            case ParquetEncoding.ByteStreamSplit:
                RequireTypes(encoding, PhysicalType.Int32, PhysicalType.Int64, PhysicalType.Float, PhysicalType.Double, PhysicalType.FixedLenByteArray);
                return;
            default:
                throw new NotSupportedException($"Column '{Column.Name}' uses the {encoding} encoding, which is not supported yet.");
        }
    }

    private void RequireTypes(ParquetEncoding encoding, params PhysicalType[] allowed)
    {
        if (Array.IndexOf(allowed, Column.Type) < 0)
        {
            throw Corrupt($"a {Column.Type} column cannot use the {encoding} encoding");
        }
    }

    /// <summary>
    /// Rewrites the values of a page encoded with a delta or byte-stream-split encoding in PLAIN layout, so that the batch
    /// methods read every encoding the same way. The converted page takes memory in proportion to its values, bounded by
    /// the limits on page size and rows per page.
    /// </summary>
    private void ConvertToPlain(ParquetEncoding encoding, ReadOnlySpan<byte> source, int count)
    {
        _converted.ResetWrittenCount();
        int fixedLength = Column.Type == PhysicalType.FixedLenByteArray ? Column.TypeLength ?? 0 : 0;
        switch (encoding)
        {
            case ParquetEncoding.DeltaBinaryPacked when Column.Type == PhysicalType.Int32:
                var ints = new int[count];
                DeltaBinaryPackedDecoder.Decode(source, ints);
                PlainEncoder.WriteInt32s(ints, _converted);
                break;
            case ParquetEncoding.DeltaBinaryPacked:
                var longs = new long[count];
                DeltaBinaryPackedDecoder.Decode(source, longs);
                PlainEncoder.WriteInt64s(longs, _converted);
                break;
            case ParquetEncoding.DeltaLengthByteArray:
                DeltaLengthByteArrayDecoder.Decode(source, count, _converted);
                break;
            case ParquetEncoding.DeltaByteArray:
                DeltaByteArrayDecoder.Decode(source, count, fixedLength, _file.Options.MaxPageSize, _converted);
                break;
            default:
                int width = Column.Type switch
                {
                    PhysicalType.Int32 or PhysicalType.Float => sizeof(int),
                    PhysicalType.Int64 or PhysicalType.Double => sizeof(long),
                    _ => fixedLength,
                };
                long size = (long)count * width;
                if (size > _file.Options.MaxPageSize)
                {
                    throw Corrupt($"{count} values of {width} bytes are over the page size limit of {_file.Options.MaxPageSize}");
                }

                var plain = _converted.GetSpan((int)size);
                ByteStreamSplitDecoder.Decode(source, count, width, plain);
                _converted.Advance((int)size);
                break;
        }

        _valueSource = _converted.WrittenMemory;
        _valuePosition = 0;
        _valueEnd = _converted.WrittenCount;
    }

    private CorruptColumnException Corrupt(string detail) => new(Column.Name, detail);

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

    private readonly struct Int96Decoder : IPlainValueDecoder<Int96>
    {
        public static int Read(ReadOnlySpan<byte> source, Span<Int96> destination) => PlainDecoder.ReadInt96s(source, destination);
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
