using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization;

/// <summary>Builds the rows of a file one row group at a time: the columns the row type needs are read into memory, then the rows are assembled from them.</summary>
internal sealed class DeserializationSession<TRow>
{
    /// <summary>The most rows a list is made room for up front on the word of the file.</summary>
    internal const int MaxPreallocatedRows = 1 << 21;

    /// <summary>How many rows to make room for on the word of the file, which a damaged file can make negative or enormous.</summary>
    internal static int PreallocatedRows(long rowsAccordingToTheFile) => (int)Math.Clamp(rowsAccordingToTheFile, 0, MaxPreallocatedRows);

    private readonly ParquetFileReader _reader;
    private readonly ReadModel<TRow> _model = ReadModel<TRow>.Get();
    private readonly ReadPlan _plan;
    private readonly ReadContext _context;

    /// <param name="filter">When there is one, the row groups it rules out by their statistics are left out of <see cref="RowGroups"/>.</param>
    /// <exception cref="Exceptions.SchemaMismatchException">The file does not have the columns the row type needs, or has them as other types.</exception>
    public DeserializationSession(ParquetFileReader reader, ParquetFilter? filter = null)
    {
        _reader = reader;
        RowGroups = filter is null ? Enumerable.Range(0, reader.RowGroupCount).ToArray() : reader.GetMatchingRowGroups(filter);
        RowCount = RowGroups.Sum(group => reader.Metadata.RowGroups[group].NumRows);
        _plan = _model.Bind(reader.Schema);
        _context = _model.CreateContext(_plan);
    }

    /// <summary>The positions of the row groups that are read, in order.</summary>
    public IReadOnlyList<int> RowGroups { get; }

    /// <summary>The number of rows in the row groups that are read, as the footer says.</summary>
    public long RowCount { get; }

    /// <summary>The byte ranges of the file that hold the column chunks of a row group that are read.</summary>
    public List<(long Start, int Length)> Chunks(int rowGroup)
    {
        var chunks = new List<(long Start, int Length)>();
        long fileLength = _reader.FileLength;
        foreach (int column in _plan.FileColumns.Where(c => c >= 0))
        {
            var meta = _reader.Metadata.RowGroups[rowGroup].Columns[column].MetaData;
            if (meta is null)
            {
                continue; // the column reader reports it
            }

            long start = meta.DictionaryPageOffset is > 0 ? meta.DictionaryPageOffset.Value : meta.DataPageOffset;
            if (start >= 4 && meta.TotalCompressedSize is >= 0 and <= int.MaxValue && start <= fileLength - meta.TotalCompressedSize)
            {
                chunks.Add((start, (int)meta.TotalCompressedSize));
            }
        }

        return Merge(chunks);
    }

    public List<TRow> ReadRowGroup(int rowGroup)
    {
        var group = _reader.OpenRowGroup(rowGroup);
        long count = group.RowCount;
        if (count > int.MaxValue)
        {
            throw new NotSupportedException($"Row group {rowGroup} has {count} rows, more than can be read into a list.");
        }

        for (int slot = 0; slot < _context.Columns.Length; slot++)
        {
            if (_plan.FileColumns[slot] < 0)
            {
                _context.Columns[slot].Fill(null);
            }
            else
            {
                using var column = group.OpenColumn(_plan.FileColumns[slot]);
                _context.Columns[slot].Fill(column);
            }
        }

        // The count is what the file says, so the list is only sized for it up to a limit, and grows past that.
        var rows = new List<TRow>(PreallocatedRows(count));
        for (long row = 0; row < count; row++)
        {
            rows.Add(_model.Row.ReadRow(_context));
        }

        // Every entry of every column is for some row; if some are left the file and the rows do not fit together.
        for (int slot = 0; slot < _context.Columns.Length; slot++)
        {
            if (_plan.FileColumns[slot] >= 0 && !_context.Columns[slot].AtEnd)
            {
                throw new CorruptColumnException(_context.Columns[slot].Name, $"it holds more entries than the {count} rows of its row group use");
            }
        }

        return rows;
    }

    /// <summary>Joins ranges that touch or overlap, so that the columns of a row group are read with as few calls as possible.</summary>
    internal static List<(long Start, int Length)> Merge(List<(long Start, int Length)> chunks)
    {
        var merged = new List<(long Start, int Length)>();
        foreach (var (start, length) in chunks.OrderBy(c => c.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                long end = Math.Max(last.Start + last.Length, start + length);
                merged[^1] = (last.Start, (int)Math.Min(end - last.Start, int.MaxValue));
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }
}
