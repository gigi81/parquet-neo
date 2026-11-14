using System.Buffers;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;

namespace ParquetNeo;

// Reading only the rows of a RowSelection: the pages with none of them are not read, and a page with some is trimmed to them.
public sealed partial class ColumnReader
{
    private RowSelection? _selection;
    private List<PageLocation>? _pageLocations;
    private bool _dictionaryRead;
    private int _nextPageLocation;
    private int _rangeIndex;
    private bool _pageLoaded; // a page is loaded and has rows after _cursorRow that were not looked at
    private long _loadedPageFirstRow;
    private long _loadedPageRows;
    private long _cursorRow; // the rows before it, of the loaded page, were taken or left out
    private int[]? _recordStarts; // for a repeated column, the entry each row of the loaded page starts at, then the entry count
    private int _scanEntry; // the number of values before this entry of the loaded page is _scanValues
    private int _scanValues;

    /// <summary>Makes the reader return only the rows of <paramref name="selection"/>; called by the constructor, before anything is read.</summary>
    internal void Select(RowSelection selection, List<PageLocation> pageLocations)
    {
        _selection = selection;
        _pageLocations = pageLocations;
    }

    /// <summary>Goes to the next stretch of selected rows: in the page that is loaded, or in the next page that has any.</summary>
    private bool LoadNextSelected()
    {
        EnsureDictionary();
        while (true)
        {
            if (_pageLoaded && SelectWindow())
            {
                return true;
            }

            if (!LoadNextSelectedPage())
            {
                _pageEntries = _pageEntryIndex = 0;
                return false;
            }
        }
    }

    /// <summary>The dictionary page, which is in front of the pages the offset index lists, is read once.</summary>
    private void EnsureDictionary()
    {
        if (_dictionaryRead)
        {
            return;
        }

        _dictionaryRead = true;
        if (_meta.DictionaryPageOffset is not > 0)
        {
            return;
        }

        var header = ReadPageHeader(out int headerLength);
        if (header.Type != PageType.DictionaryPage)
        {
            return;
        }

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

        ReadDictionaryPage(header, dataStart, compressedSize, uncompressedSize);
    }

    /// <summary>Loads the next page that has a selected row, leaving out the ones before it; false when there is none.</summary>
    private bool LoadNextSelectedPage()
    {
        var ranges = _selection!.Ranges;
        var pages = _pageLocations!;
        while (_nextPageLocation < pages.Count)
        {
            long first = pages[_nextPageLocation].FirstRowIndex;
            long end = _nextPageLocation + 1 < pages.Count ? pages[_nextPageLocation + 1].FirstRowIndex : _rowCount;
            while (_rangeIndex < ranges.Count && ranges[_rangeIndex].End <= first)
            {
                _rangeIndex++;
            }

            if (_rangeIndex >= ranges.Count)
            {
                return false;
            }

            if (ranges[_rangeIndex].Start >= end)
            {
                _nextPageLocation++;
                continue;
            }

            long recordsBefore = _recordsLoaded;
            _position = pages[_nextPageLocation].Offset;
            _nextPageLocation++;
            if (!LoadNextPage(sequential: true))
            {
                throw Corrupt("its offset index lists a page that is not in the column chunk");
            }

            _loadedPageFirstRow = first;
            _loadedPageRows = end - first;
            if (_recordsLoaded - recordsBefore != _loadedPageRows)
            {
                throw Corrupt($"a page holds {_recordsLoaded - recordsBefore} rows but its offset index says {_loadedPageRows}");
            }

            _cursorRow = first;
            _scanEntry = _scanValues = 0;
            if (_maxRepetitionLevel > 0)
            {
                IndexRecords();
            }

            _pageLoaded = true;
            return true;
        }

        return false;
    }

    /// <summary>For a repeated column: where each row of the loaded page starts, as the position of its first entry.</summary>
    private void IndexRecords()
    {
        int rows = checked((int)_loadedPageRows);
        EnsureCapacity(ref _recordStarts, rows + 1);
        var starts = _recordStarts!;
        int row = 0;
        var repetition = _repetitionLevels.AsSpan(0, _pageEntries);
        for (int entry = 0; entry < repetition.Length; entry++)
        {
            if (repetition[entry] == 0)
            {
                starts[row++] = entry;
            }
        }

        starts[rows] = _pageEntries;
    }

    /// <summary>Makes the next selected rows of the loaded page what is read next: false when it has none left.</summary>
    private bool SelectWindow()
    {
        var ranges = _selection!.Ranges;
        long pageEnd = _loadedPageFirstRow + _loadedPageRows;
        while (_rangeIndex < ranges.Count && ranges[_rangeIndex].End <= _cursorRow)
        {
            _rangeIndex++;
        }

        if (_rangeIndex >= ranges.Count || Math.Max(ranges[_rangeIndex].Start, _cursorRow) >= pageEnd)
        {
            _pageLoaded = false;
            return false;
        }

        long from = Math.Max(ranges[_rangeIndex].Start, _cursorRow);
        long to = Math.Min(ranges[_rangeIndex].End, pageEnd);
        int entryStart = EntryOf(from);
        int entryEnd = EntryOf(to);
        int valueStart = ValuesBefore(entryStart);
        SkipValues(valueStart - _pageValueIndex);
        _pageEntryIndex = entryStart;
        _pageValueIndex = valueStart;
        _pageEntries = entryEnd;
        _cursorRow = to;
        return true;
    }

    private int EntryOf(long row) =>
        _maxRepetitionLevel == 0 ? (int)(row - _loadedPageFirstRow) : _recordStarts![(int)(row - _loadedPageFirstRow)];

    /// <summary>The number of values (entries that are not null) before an entry of the loaded page; entries are asked for in ascending order.</summary>
    private int ValuesBefore(int entry)
    {
        if (_maxDefinitionLevel == 0)
        {
            return entry;
        }

        _scanValues += _levels.AsSpan(_scanEntry, entry - _scanEntry).Count(_maxDefinitionLevel);
        _scanEntry = entry;
        return _scanValues;
    }

    /// <summary>Moves over values of the page that are not wanted, so that the next one read is the one after them.</summary>
    private void SkipValues(int count)
    {
        if (count <= 0 || _pageIsDictionary || Column.Type == PhysicalType.Boolean)
        {
            return; // these are read by position, not in sequence
        }

        switch (Column.Type)
        {
            case PhysicalType.Int32 or PhysicalType.Float:
                _valuePosition += count * sizeof(int);
                break;
            case PhysicalType.Int64 or PhysicalType.Double:
                _valuePosition += count * sizeof(long);
                break;
            case PhysicalType.Int96:
                _valuePosition += count * 12;
                break;
            case PhysicalType.FixedLenByteArray:
                _valuePosition += count * (Column.TypeLength ?? 0);
                break;
            default:
                Span<ByteRange> ranges = stackalloc ByteRange[256];
                while (count > 0)
                {
                    int step = Math.Min(ranges.Length, count);
                    _valuePosition += PlainDecoder.ReadByteArrays(_valueSource.Span.Slice(_valuePosition, _valueEnd - _valuePosition), ranges[..step]);
                    count -= step;
                }

                break;
        }
    }
}
