using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Serialization;

public static partial class ParquetSerializer
{
    /// <summary>Reads all the rows of a Parquet file.</summary>
    /// <param name="source">A readable, seekable stream holding the whole file. It is not closed.</param>
    /// <exception cref="SchemaMismatchException">The file does not have a column for a property that cannot be null, or has it as a type the property cannot hold.</exception>
    /// <exception cref="ParquetException">The file is malformed.</exception>
    public static List<TRow> Deserialize<TRow>(Stream source, ParquetDeserializerOptions? options = null)
    {
        RequireReadable(source);
        using var reader = new ParquetFileReader(source, (options ?? new ParquetDeserializerOptions()).Reader, leaveOpen: true);
        var session = new DeserializationSession<TRow>(reader);
        var rows = new List<TRow>(DeserializationSession<TRow>.PreallocatedRows(session.RowCount));
        for (int group = 0; group < session.RowGroupCount; group++)
        {
            rows.AddRange(session.ReadRowGroup(group));
        }

        return rows;
    }

    /// <summary>Reads the rows of a Parquet file as they are asked for, one row group at a time.</summary>
    /// <remarks>
    /// The columns of a row group that the row type needs are read into memory when the first of its rows is asked for, so the
    /// memory is that of a row group, not of the file. The file is opened, and the rows can fail to match its schema, when the
    /// first row is asked for. Columns of the file that the row type has no property for are never read.
    /// </remarks>
    /// <param name="source">A readable, seekable stream holding the whole file. It is not closed.</param>
    public static IEnumerable<TRow> DeserializeAll<TRow>(Stream source, ParquetDeserializerOptions? options = null)
    {
        RequireReadable(source);
        return ReadAll<TRow>(source, options ?? new ParquetDeserializerOptions());
    }

    /// <summary>Reads all the rows of a Parquet file, without blocking while the stream is read.</summary>
    /// <param name="source">A readable, seekable stream holding the whole file. It is not closed.</param>
    /// <exception cref="SchemaMismatchException">The file does not have a column for a property that cannot be null, or has it as a type the property cannot hold.</exception>
    public static async Task<List<TRow>> DeserializeAsync<TRow>(Stream source, ParquetDeserializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<TRow>();
        await foreach (var row in DeserializeAllAsync<TRow>(source, options, cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Reads the rows of a Parquet file as they are asked for, one row group at a time, without blocking while the stream is read.
    /// Only the bytes of the footer and of the columns the row type needs are read from the stream.
    /// </summary>
    /// <param name="source">A readable, seekable stream holding the whole file. It is not closed.</param>
    public static IAsyncEnumerable<TRow> DeserializeAllAsync<TRow>(Stream source, ParquetDeserializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireReadable(source);
        return ReadAllAsync<TRow>(source, options ?? new ParquetDeserializerOptions(), cancellationToken);
    }

    private static IEnumerable<TRow> ReadAll<TRow>(Stream source, ParquetDeserializerOptions options)
    {
        using var reader = new ParquetFileReader(source, options.Reader, leaveOpen: true);
        var session = new DeserializationSession<TRow>(reader);
        for (int group = 0; group < session.RowGroupCount; group++)
        {
            foreach (var row in session.ReadRowGroup(group))
            {
                yield return row;
            }
        }
    }

    private static async IAsyncEnumerable<TRow> ReadAllAsync<TRow>(Stream source, ParquetDeserializerOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prefetched = new PrefetchedStream(source.Length);
        await LoadFooterAsync(source, prefetched, options, cancellationToken).ConfigureAwait(false);
        using var reader = new ParquetFileReader(prefetched, options.Reader, leaveOpen: true);
        var session = new DeserializationSession<TRow>(reader);
        for (int group = 0; group < session.RowGroupCount; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (start, length) in session.Chunks(group))
            {
                await prefetched.LoadChunkAsync(source, start, length, cancellationToken).ConfigureAwait(false);
            }

            var rows = session.ReadRowGroup(group);
            prefetched.DropLoaded();
            foreach (var row in rows)
            {
                yield return row;
            }
        }
    }

    /// <summary>Reads the markers and the footer, which the reader needs to open the file, unless they are wrong, which the reader then reports.</summary>
    private static async Task LoadFooterAsync(Stream source, PrefetchedStream prefetched, ParquetDeserializerOptions options, CancellationToken cancellationToken)
    {
        long length = source.Length;
        if (length < 12)
        {
            return;
        }

        await prefetched.LoadKeptAsync(source, 0, 4, cancellationToken).ConfigureAwait(false);
        await prefetched.LoadKeptAsync(source, length - 8, 8, cancellationToken).ConfigureAwait(false);
        int footerLength = prefetched.ReadInt32(length - 8);
        if (footerLength > 0 && footerLength <= length - 12 && footerLength <= options.Reader.MaxFooterSize)
        {
            await prefetched.LoadKeptAsync(source, length - 8 - footerLength, footerLength, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireReadable(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(source));
        }
    }
}
