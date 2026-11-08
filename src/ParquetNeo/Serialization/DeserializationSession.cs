using ParquetNeo.Core;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization;

/// <summary>Builds the rows of a file one row group at a time: the columns the row type needs are read into memory, then the rows are assembled from them.</summary>
internal sealed class DeserializationSession<TRow>
{
    private readonly ParquetFileReader _reader;
    private readonly ReadModel<TRow> _model = ReadModel<TRow>.Get();
    private readonly int[] _fileColumns;
    private readonly ILeafColumn[] _columns;
    private readonly RowContext<TRow> _context = new();

    /// <exception cref="Exceptions.SchemaMismatchException">The file does not have the columns the row type needs, or has them as other types.</exception>
    public DeserializationSession(ParquetFileReader reader)
    {
        _reader = reader;
        _fileColumns = _model.Bind(reader.Schema);
        _columns = _model.CreateColumns();
    }

    public int RowGroupCount => _reader.RowGroupCount;

    /// <summary>The byte ranges of the file that hold the column chunks of a row group that are read.</summary>
    public List<(long Start, int Length)> Chunks(int rowGroup)
    {
        var chunks = new List<(long Start, int Length)>();
        long fileLength = _reader.FileLength;
        foreach (int column in _fileColumns.Where(c => c >= 0))
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

        for (int i = 0; i < _columns.Length; i++)
        {
            if (_fileColumns[i] < 0)
            {
                _columns[i].Fill(null, count);
            }
            else
            {
                using var column = group.OpenColumn(_fileColumns[i]);
                _columns[i].Fill(column, count);
            }
        }

        var rows = new List<TRow>();
        for (long row = 0; row < count; row++)
        {
            rows.Add(_model.Row.Read(_columns, _context));
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
