using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ParquetNeo.Compression;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Nested;
using ParquetNeo.Schema;
using System.Text;
using ParquetNeo.Core.Statistics;

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
/// <para>
/// A nested column (a leaf below optional or repeated groups) has a definition level per entry that counts how many
/// of its optional and repeated ancestors, and itself, are present, up to <see cref="PrimitiveNode.MaxDefinitionLevel"/>:
/// only entries at that level carry a value. A column inside a repeated group also takes a repetition level per entry,
/// which says at which repeated ancestor the entry repeats: <c>0</c> starts a new row, and a higher level continues the
/// row at that depth. Entries are the unit of a batch; a row can span several entries and even several batches (it ends
/// where the next entry with repetition level <c>0</c> begins), and the first entry of a column must start a row.
/// Pages never end in the middle of a row. The levels are checked against their ranges but not against the schema's
/// structure: levels that no row could produce give a file readers will reject.
/// </para>
/// <para>Values are buffered into pages of about <see cref="ParquetWriterOptions.DataPageSize"/> bytes, which are
/// compressed and written to the stream as they fill. Instances are not thread safe.</para>
/// </remarks>
public sealed partial class ColumnWriter
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
    private readonly int _maxRepetitionLevel;
    private readonly int _repetitionBitWidth;

    // Non-null while the chunk is dictionary encoded. Its data pages are encoded as they fill but held back,
    // because the dictionary page, which is only complete at the end, has to come first in the file.
    private DictionaryBuilder? _dictionary;
    private readonly List<EncodedPage> _pending = new();
    private byte[]? _stringScratch;
    private readonly StatisticsCollector? _statistics;
    private long _valueCount; // non-null values written, so that rows minus values is the null count

    private int _pageLevelCount;
    private int _pageLogicalBytes; // what the page's values would take PLAIN encoded, used to decide when a page is full
    private long _entries; // level entries written: one per value, null, empty list and so on
    private long _records; // rows: entries that start a new record (all of them in a column without repetition)
    private bool _pageActionsDeferred; // a page flush or dictionary fallback is due but must wait for the end of a record
    private long _firstPageOffset = -1;
    private long _dataPageOffset = -1;
    private long _dictionaryPageOffset = -1;
    private long _totalUncompressed;
    private long _totalCompressed;
    private int _dictionaryPages;
    private int _dictionaryEncodedPages;
    private int _plainPages;
    private readonly ParquetEncoding _valueEncoding; // for the pages that are not dictionary encoded
    private readonly bool _dataPageV2;
    private int _pageRecords; // rows that start in the page being filled
    private int _pageNullCount;
    private int _levelRepetitionBytes; // of the page being flushed (data page v2 states the lengths of its level sections)
    private int _levelDefinitionBytes;
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
        _maxRepetitionLevel = column.MaxRepetitionLevel;
        _repetitionBitWidth = 32 - BitOperations.LeadingZeroCount((uint)_maxRepetitionLevel);
        _dictionary = options.DictionaryEnabled ? CreateDictionary(column.Type, column.TypeLength) : null;
        _statistics = StatisticsFactory.Create(column, options);
        _valueEncoding = ValueEncodingRules.For(column, options.ColumnEncodings);
        _dataPageV2 = options.DataPageVersion == DataPageVersion.V2;
        _buffers.Reset();
    }

    /// <summary>The column this writer fills.</summary>
    public PrimitiveNode Column { get; }

    /// <summary>The number of rows (records) written so far. In a repeated column a row can span several entries.</summary>
    public long RowCount => _records;

    /// <summary>The number of level entries written so far: one per value, null, empty list and so on. Equals <see cref="RowCount"/> unless the column is repeated.</summary>
    public long EntryCount => _entries;

    private bool HasLevels => _maxDefinitionLevel > 0;

    private bool HasRepetition => _maxRepetitionLevel > 0;

    private int RowsFree => MaxRowsPerPage - _pageLevelCount;

    private int BufferedBytes => Column.Type == PhysicalType.Boolean ? (_buffers.Booleans.WrittenCount + 7) / 8 : _buffers.Values.WrittenCount;

    /// <summary>How full the current page is, in the bytes its values take PLAIN encoded (even when they are dictionary encoded).</summary>
    private int PageBytes => _dictionary is not null ? _pageLogicalBytes : BufferedBytes;

    /// <summary>Writes INT32 values; see the class remarks for the levels.</summary>
    public void WriteBatch(ReadOnlySpan<int> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteFixedWidth<int, Int32Encoder>(PhysicalType.Int32, values, definitionLevels, repetitionLevels, sizeof(int));

    public void WriteBatch(ReadOnlySpan<long> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteFixedWidth<long, Int64Encoder>(PhysicalType.Int64, values, definitionLevels, repetitionLevels, sizeof(long));

    public void WriteBatch(ReadOnlySpan<float> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteFixedWidth<float, SingleEncoder>(PhysicalType.Float, values, definitionLevels, repetitionLevels, sizeof(float));

    public void WriteBatch(ReadOnlySpan<double> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteFixedWidth<double, DoubleEncoder>(PhysicalType.Double, values, definitionLevels, repetitionLevels, sizeof(double));

    public void WriteBatch(ReadOnlySpan<bool> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        int entries = PrepareBatch(PhysicalType.Boolean, values.Length, definitionLevels, repetitionLevels);
        int entry = 0;
        int next = 0;
        while (entry < entries)
        {
            // Booleans are bit-packed per page, so they are collected here and packed when the page closes.
            long remainingBits = Math.Max(1L, (long)(_pageSize - PageBytes) * 8);
            int take = (int)Math.Min(Math.Min(entries - entry, remainingBits), RowsFree);
            take = ExtendToRecordEnd(repetitionLevels, entry, take, entries);
            int valueCount = AppendLevels(definitionLevels, repetitionLevels, entry, take);
            _buffers.Booleans.Write(values.Slice(next, valueCount));
            _statistics?.Add(values.Slice(next, valueCount));
            _valueCount += valueCount;
            CompleteChunk(take, atBatchEnd: entry + take == entries);
            entry += take;
            next += valueCount;
        }
    }

    /// <summary>Writes BYTE_ARRAY values; the memory of each value is read immediately and not retained.</summary>
    public void WriteByteArrays(ReadOnlySpan<ReadOnlyMemory<byte>> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        int entries = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels, repetitionLevels);
        int entry = 0;
        int next = 0;
        while (entry < entries)
        {
            int start = entry;
            while (entry < entries)
            {
                if (!HasLevels || definitionLevels[entry] == _maxDefinitionLevel)
                {
                    AppendByteArray(values[next++].Span);
                }

                entry++;
                if (PageIsFull(start, entry) && AtRecordStart(repetitionLevels, entry, entries))
                {
                    break;
                }
            }

            AppendLevelsAndComplete(definitionLevels, repetitionLevels, start, entry - start, atBatchEnd: entry == entries);
        }
    }

    /// <summary>Writes BYTE_ARRAY values from strings, encoded as UTF-8 straight into the page buffer.</summary>
    public void WriteStrings(ReadOnlySpan<string> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        int entries = PrepareBatch(PhysicalType.ByteArray, values.Length, definitionLevels, repetitionLevels);
        int entry = 0;
        int next = 0;
        while (entry < entries)
        {
            int start = entry;
            while (entry < entries)
            {
                if (!HasLevels || definitionLevels[entry] == _maxDefinitionLevel)
                {
                    AppendString(values[next++]);
                }

                entry++;
                if (PageIsFull(start, entry) && AtRecordStart(repetitionLevels, entry, entries))
                {
                    break;
                }
            }

            AppendLevelsAndComplete(definitionLevels, repetitionLevels, start, entry - start, atBatchEnd: entry == entries);
        }
    }

    /// <summary>Writes FIXED_LEN_BYTE_ARRAY values given back to back; the length must be a multiple of the column's type length.</summary>
    public void WriteFixedLengthByteArrays(ReadOnlySpan<byte> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        int length = Column.TypeLength ?? 0;
        if (length == 0 || values.Length % length != 0)
        {
            throw new ArgumentException($"The values must be a whole number of {length}-byte entries.", nameof(values));
        }

        int entries = PrepareBatch(PhysicalType.FixedLenByteArray, values.Length / length, definitionLevels, repetitionLevels);
        int entry = 0;
        int next = 0;
        while (entry < entries)
        {
            int take = (int)Math.Min(Math.Min(entries - entry, Math.Max(1, (_pageSize - PageBytes) / length)), RowsFree);
            take = ExtendToRecordEnd(repetitionLevels, entry, take, entries);
            int valueCount = AppendLevels(definitionLevels, repetitionLevels, entry, take);
            var chunk = values.Slice(next * length, valueCount * length);
            if (_statistics is not null)
            {
                for (int i = 0; i < valueCount; i++)
                {
                    _statistics.AddBinary(chunk.Slice(i * length, length));
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

            CompleteChunk(take, atBatchEnd: entry + take == entries);
            entry += take;
            next += valueCount;
        }
    }

    // ---- the same, with the shape of the data given as a ColumnStructure instead of levels ----

    /// <summary>Writes INT32 values whose nesting is given as validity and offsets; the structure supplies the levels.</summary>
    public void WriteBatch(ReadOnlySpan<int> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteBatch(values, definition, repetition);
    }

    public void WriteBatch(ReadOnlySpan<long> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteBatch(values, definition, repetition);
    }

    public void WriteBatch(ReadOnlySpan<float> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteBatch(values, definition, repetition);
    }

    public void WriteBatch(ReadOnlySpan<double> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteBatch(values, definition, repetition);
    }

    public void WriteBatch(ReadOnlySpan<bool> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteBatch(values, definition, repetition);
    }

    public void WriteByteArrays(ReadOnlySpan<ReadOnlyMemory<byte>> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteByteArrays(values, definition, repetition);
    }

    public void WriteStrings(ReadOnlySpan<string> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteStrings(values, definition, repetition);
    }

    public void WriteFixedLengthByteArrays(ReadOnlySpan<byte> values, ColumnStructure structure)
    {
        var (definition, repetition) = LevelsOf(structure);
        WriteFixedLengthByteArrays(values, definition, repetition);
    }

    private (int[] Definition, int[] Repetition) LevelsOf(ColumnStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        if (!ReferenceEquals(structure.Column, Column))
        {
            throw new ArgumentException($"The structure is for column '{string.Join('.', structure.Column.Path)}', not '{string.Join('.', Column.Path)}'.", nameof(structure));
        }

        structure.GetLevels(out int[] definition, out int[] repetition);
        return (definition, repetition);
    }

    internal ColumnChunk Complete()
    {
        EnsureOpen();
        FlushPage();
        if (_dictionary is not null)
        {
            // The chunk stayed dictionary encoded to the end: the dictionary page and the held-back pages go out now.
            if (_entries > 0)
            {
                WriteDictionaryChunk();
            }

            _dictionary = null;
        }

        _completed = true;
        if (_entries == 0)
        {
            throw new InvalidOperationException($"No values were written to column '{Column.Name}'.");
        }

        // Every encoding some page uses: the dictionary page is PLAIN, and the other pages use the column's own encoding.
        var encodings = new List<ParquetEncoding>();
        if (_dictionaryPages > 0 || _plainPages > 0)
        {
            encodings.Add(_dictionaryPages > 0 ? ParquetEncoding.Plain : _valueEncoding);
        }

        if (_plainPages > 0 && !encodings.Contains(_valueEncoding))
        {
            encodings.Add(_valueEncoding);
        }

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
            pageStats.Add(new PageEncodingStats { PageType = _dataPageV2 ? PageType.DataPageV2 : PageType.DataPage, Encoding = ParquetEncoding.RleDictionary, Count = _dictionaryEncodedPages });
        }

        if (_plainPages > 0)
        {
            pageStats.Add(new PageEncodingStats { PageType = _dataPageV2 ? PageType.DataPageV2 : PageType.DataPage, Encoding = _valueEncoding, Count = _plainPages });
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
                NumValues = _entries,
                TotalUncompressedSize = _totalUncompressed,
                TotalCompressedSize = _totalCompressed,
                DataPageOffset = _dataPageOffset,
                DictionaryPageOffset = _dictionaryPageOffset >= 0 ? _dictionaryPageOffset : null,
                EncodingStats = pageStats,
                Statistics = _statistics?.Build(_entries - _valueCount),
            },
        };
    }

    internal long TotalUncompressedSize => _totalUncompressed;

    internal long TotalCompressedSize => _totalCompressed;

    /// <summary>Where the chunk starts: its dictionary page if it has one, otherwise its first data page.</summary>
    internal long FirstPageOffset => _firstPageOffset;

    private void WriteFixedWidth<T, TEncoder>(PhysicalType expected, ReadOnlySpan<T> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels, int valueBytes)
        where T : unmanaged
        where TEncoder : IPlainValueEncoder<T>
    {
        int entries = PrepareBatch(expected, values.Length, definitionLevels, repetitionLevels);
        int entry = 0;
        int next = 0;
        while (entry < entries)
        {
            // As many entries as fit in the rest of the page (at least one), so pages close close to the target size.
            int take = (int)Math.Min(Math.Min(entries - entry, Math.Max(1, (_pageSize - PageBytes) / valueBytes)), RowsFree);
            take = ExtendToRecordEnd(repetitionLevels, entry, take, entries);
            int valueCount = AppendLevels(definitionLevels, repetitionLevels, entry, take);
            var chunk = values.Slice(next, valueCount);
            _statistics?.Add(chunk);
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

            CompleteChunk(take, atBatchEnd: entry + take == entries);
            entry += take;
            next += valueCount;
        }
    }

    private void AppendByteArray(ReadOnlySpan<byte> value)
    {
        _statistics?.AddBinary(value);
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
            if (_statistics is not null)
            {
                // The string was encoded straight into the page buffer; the statistics read it back from there.
                _statistics.AddBinary(_buffers.Values.WrittenSpan[(start + sizeof(int))..]);
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
        _statistics?.AddBinary(_stringScratch.AsSpan(0, length));
        AddIndex(dictionary.GetOrAdd(_stringScratch.AsSpan(0, length)));
        _pageLogicalBytes += sizeof(int) + length;
    }

    private void AddIndex(int index)
    {
        var span = _buffers.Indices.GetSpan(1);
        span[0] = index;
        _buffers.Indices.Advance(1);
    }

    /// <summary>Checks a batch and returns its number of level entries.</summary>
    private int PrepareBatch(PhysicalType expected, int valueCount, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        EnsureOpen();
        if (Column.Type != expected)
        {
            throw new ArgumentException($"Column '{Column.Name}' is {Column.Type}, so it cannot take {expected} values.");
        }

        int entries;
        if (!HasLevels)
        {
            if (!definitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is required, so it takes no definition levels.", nameof(definitionLevels));
            }

            entries = valueCount;
        }
        else if (definitionLevels.IsEmpty)
        {
            if (valueCount != 0)
            {
                throw new ArgumentException($"Column '{Column.Name}' is optional, so it needs one definition level per entry.", nameof(definitionLevels));
            }

            entries = 0;
        }
        else
        {
            if (definitionLevels.IndexOfAnyExceptInRange(0, _maxDefinitionLevel) >= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(definitionLevels), $"Definition levels of column '{Column.Name}' must be between 0 and {_maxDefinitionLevel}.");
            }

            int present = definitionLevels.Count(_maxDefinitionLevel);
            if (present != valueCount)
            {
                throw new ArgumentException($"{present} entries are marked non-null but {valueCount} values were given.", nameof(definitionLevels));
            }

            entries = definitionLevels.Length;
        }

        if (!HasRepetition)
        {
            if (!repetitionLevels.IsEmpty)
            {
                throw new ArgumentException($"Column '{Column.Name}' is not repeated, so it takes no repetition levels.", nameof(repetitionLevels));
            }

            return entries;
        }

        if (repetitionLevels.Length != entries)
        {
            throw new ArgumentException($"Column '{Column.Name}' is repeated, so it needs one repetition level per definition level ({repetitionLevels.Length} given for {entries} entries).", nameof(repetitionLevels));
        }

        if (entries > 0)
        {
            if (repetitionLevels.IndexOfAnyExceptInRange(0, _maxRepetitionLevel) >= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(repetitionLevels), $"Repetition levels of column '{Column.Name}' must be between 0 and {_maxRepetitionLevel}.");
            }

            if (_entries == 0 && repetitionLevels[0] != 0)
            {
                throw new ArgumentException("The first entry of a column must start a row, so its repetition level must be 0.", nameof(repetitionLevels));
            }

            // A page flush that had to wait for the end of a record can happen now if this batch starts a new one.
            if (_pageActionsDeferred && repetitionLevels[0] == 0)
            {
                ApplyPageActions();
            }
        }

        return entries;
    }

    /// <summary>Whether the page being filled has reached its target size (counting rows as well as bytes).</summary>
    private bool PageIsFull(int batchStart, int batchEnd) =>
        PageBytes >= _pageSize || _pageLevelCount + (batchEnd - batchStart) >= MaxRowsPerPage;

    /// <summary>Whether a page may end before entry <paramref name="entry"/>: the batch ends there, or a new record starts.</summary>
    private bool AtRecordStart(ReadOnlySpan<int> repetitionLevels, int entry, int entries) =>
        !HasRepetition || entry == entries || repetitionLevels[entry] == 0;

    /// <summary>
    /// Pages must not end in the middle of a record, so a chunk of <paramref name="take"/> entries starting at
    /// <paramref name="entry"/> is extended to the next entry that starts a record (or the end of the batch).
    /// </summary>
    private int ExtendToRecordEnd(ReadOnlySpan<int> repetitionLevels, int entry, int take, int entries)
    {
        if (!HasRepetition)
        {
            return take;
        }

        int end = entry + take;
        if (end >= entries)
        {
            return entries - entry;
        }

        int boundary = repetitionLevels.Slice(end).IndexOf(0);
        return boundary < 0 ? entries - entry : end + boundary - entry;
    }

    /// <summary>Buffers the levels of entries [<paramref name="entry"/>, +<paramref name="take"/>) and returns how many of them hold a value.</summary>
    private int AppendLevels(ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels, int entry, int take)
    {
        int rows = take;
        if (HasRepetition)
        {
            var repetition = repetitionLevels.Slice(entry, take);
            _buffers.RepetitionLevels.Write(repetition);
            rows = repetition.Count(0);
        }

        _records += rows;
        _pageRecords += rows;

        if (!HasLevels)
        {
            return take;
        }

        var slice = definitionLevels.Slice(entry, take);
        _buffers.Levels.Write(slice);
        return slice.Count(_maxDefinitionLevel);
    }

    private void AppendLevelsAndComplete(ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels, int start, int count, bool atBatchEnd)
    {
        AppendLevels(definitionLevels, repetitionLevels, start, count);
        CompleteChunk(count, atBatchEnd);
    }

    /// <param name="atBatchEnd">
    /// Whether the chunk ends the batch. Then it is not known whether the record continues in the next batch, so in a
    /// repeated column anything that would close the page waits until the next batch shows where the record ends.
    /// </param>
    private void CompleteChunk(int levelCount, bool atBatchEnd)
    {
        _pageLevelCount += levelCount;
        _entries += levelCount;
        if (HasRepetition && atBatchEnd)
        {
            _pageActionsDeferred |= PageBytes >= _pageSize || _pageLevelCount >= MaxRowsPerPage || DictionaryIsTooBig;
            return;
        }

        ApplyPageActions();
    }

    private bool DictionaryIsTooBig => _dictionary is not null && _dictionary.EncodedSize > _dictionaryLimit;

    /// <summary>Closes the page if it is full and gives up on the dictionary if it is too big; only at the end of a record.</summary>
    private void ApplyPageActions()
    {
        if (_pageActionsDeferred || PageBytes >= _pageSize || _pageLevelCount >= MaxRowsPerPage)
        {
            FlushPage();
        }

        if (DictionaryIsTooBig)
        {
            FallBackToPlain();
        }

        _pageActionsDeferred = false;
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
        _pageActionsDeferred = false;
        if (_pageLevelCount == 0)
        {
            return;
        }

        // Data page v1: [repetition levels][definition levels][values], each level section as a 4-byte length followed
        // by RLE data; data page v2 has the same sections without the lengths (the header states them). A column without
        // repetition or definition levels has no such section.
        Span<byte> prefixStorage = stackalloc byte[sizeof(int)];
        scoped ReadOnlySpan<byte> levelPrefix = default;
        var levelData = ReadOnlySpan<byte>.Empty;
        _levelRepetitionBytes = _levelDefinitionBytes = 0;
        _pageNullCount = HasLevels ? _pageLevelCount - _buffers.Levels.WrittenSpan.Count(_maxDefinitionLevel) : 0;
        if (_dataPageV2)
        {
            var block = _buffers.LevelBlock;
            if (HasRepetition)
            {
                RleBitPackedEncoder.Encode(_buffers.RepetitionLevels.WrittenSpan, _repetitionBitWidth, _buffers.RepetitionBytes);
                block.Write(_buffers.RepetitionBytes.WrittenSpan);
                _levelRepetitionBytes = _buffers.RepetitionBytes.WrittenCount;
            }

            if (HasLevels)
            {
                RleBitPackedEncoder.Encode(_buffers.Levels.WrittenSpan, _levelBitWidth, _buffers.LevelBytes);
                block.Write(_buffers.LevelBytes.WrittenSpan);
                _levelDefinitionBytes = _buffers.LevelBytes.WrittenCount;
            }

            levelData = block.WrittenSpan;
        }
        else if (HasRepetition)
        {
            RleBitPackedEncoder.Encode(_buffers.RepetitionLevels.WrittenSpan, _repetitionBitWidth, _buffers.RepetitionBytes);
            RleBitPackedEncoder.Encode(_buffers.Levels.WrittenSpan, _levelBitWidth, _buffers.LevelBytes);
            var block = _buffers.LevelBlock;
            AppendSection(block, _buffers.RepetitionBytes.WrittenSpan);
            AppendSection(block, _buffers.LevelBytes.WrittenSpan);
            levelData = block.WrittenSpan;
        }
        else if (HasLevels)
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
        _pageRecords = 0;
        _buffers.Reset();
    }

    private static void AppendSection(ArrayBufferWriter<byte> block, ReadOnlySpan<byte> encodedLevels)
    {
        BinaryPrimitives.WriteInt32LittleEndian(block.GetSpan(sizeof(int)), encodedLevels.Length);
        block.Advance(sizeof(int));
        block.Write(encodedLevels);
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
        if (_valueEncoding != ParquetEncoding.Plain)
        {
            values = ConvertValues(values);
        }

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
            int headerLength = WriteHeader(PageType.DataPage, _pageLevelCount, _valueEncoding, uncompressedSize, uncompressedSize);
            _file.Write(_buffers.Header.WrittenSpan);
            _file.Write(levelPrefix);
            _file.Write(levelData);
            _file.Write(values);
            Account(headerLength, uncompressedSize, uncompressedSize);
        }
        else
        {
            WriteEncoded(BuildPage(PageType.DataPage, _pageLevelCount, _valueEncoding, levelPrefix, levelData, values));
        }

        _plainPages++;
    }

    /// <summary>Converts the PLAIN values of the page to the encoding chosen for the column.</summary>
    private ReadOnlySpan<byte> ConvertValues(ReadOnlySpan<byte> plain)
    {
        var encoded = _buffers.Encoded;
        encoded.ResetWrittenCount();
        int count = HasLevels ? _buffers.Levels.WrittenSpan.Count(_maxDefinitionLevel) : _pageLevelCount;
        int fixedLength = Column.Type == PhysicalType.FixedLenByteArray ? Column.TypeLength ?? 0 : 0;
        switch (_valueEncoding)
        {
            case ParquetEncoding.DeltaBinaryPacked when Column.Type == PhysicalType.Int32:
                DeltaBinaryPackedEncoder.Encode(MemoryMarshal.Cast<byte, int>(plain), encoded);
                break;
            case ParquetEncoding.DeltaBinaryPacked:
                DeltaBinaryPackedEncoder.Encode(MemoryMarshal.Cast<byte, long>(plain), encoded);
                break;
            case ParquetEncoding.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoder.Encode(plain, count, encoded);
                break;
            case ParquetEncoding.DeltaByteArray:
                DeltaByteArrayEncoder.Encode(plain, count, fixedLength, encoded);
                break;
            default:
                int width = Column.Type switch
                {
                    PhysicalType.Int32 or PhysicalType.Float => sizeof(int),
                    PhysicalType.Int64 or PhysicalType.Double => sizeof(long),
                    _ => fixedLength,
                };
                ByteStreamSplitEncoder.Encode(plain, width, encoded);
                break;
        }

        return encoded.WrittenSpan;
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

        if (_dataPageV2 && type == PageType.DataPage)
        {
            return BuildPageV2(numValues, encoding, levelData, values);
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

    /// <summary>Builds a data page v2: the levels as they are, followed by the values, which are the only part compressed.</summary>
    private EncodedPage BuildPageV2(int numValues, ParquetEncoding encoding, ReadOnlySpan<byte> levels, ReadOnlySpan<byte> values)
    {
        var pool = ArrayPool<byte>.Shared;
        byte[]? compressed = null;
        int storedLength = values.Length;
        if (_codec.Id != CompressionCodec.Uncompressed)
        {
            compressed = pool.Rent(_codec.GetMaxCompressedLength(values.Length));
            storedLength = _codec.Compress(values, compressed);
        }

        int uncompressedSize = levels.Length + values.Length;
        int compressedSize = levels.Length + storedLength;
        int headerLength = WriteHeader(PageType.DataPage, numValues, encoding, uncompressedSize, compressedSize);
        byte[] page = pool.Rent(headerLength + compressedSize);
        _buffers.Header.WrittenSpan.CopyTo(page);
        levels.CopyTo(page.AsSpan(headerLength));
        if (compressed is null)
        {
            values.CopyTo(page.AsSpan(headerLength + levels.Length));
        }
        else
        {
            compressed.AsSpan(0, storedLength).CopyTo(page.AsSpan(headerLength + levels.Length));
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
        else if (_dataPageV2)
        {
            header.Type = PageType.DataPageV2;
            header.DataPageHeaderV2 = new DataPageHeaderV2
            {
                NumValues = numValues,
                NumNulls = _pageNullCount,
                NumRows = _pageRecords,
                Encoding = encoding,
                DefinitionLevelsByteLength = _levelDefinitionBytes,
                RepetitionLevelsByteLength = _levelRepetitionBytes,
                IsCompressed = _codec.Id != CompressionCodec.Uncompressed,
            };
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
