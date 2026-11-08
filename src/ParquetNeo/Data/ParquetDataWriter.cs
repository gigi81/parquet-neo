using System.Data;
using System.Data.Common;

namespace ParquetNeo.Data;

/// <summary>
/// Writes the rows of an <see cref="IDataReader"/> (a database query, a <see cref="DataTable"/> through
/// <see cref="DataTable.CreateDataReader()"/>, or any other reader) as a Parquet file. Each column of the reader becomes a column
/// of the file, typed by <see cref="IDataRecord.GetFieldType"/>: the types of <see cref="ColumnTypes.ColumnTypeRegistry"/>, which
/// are the ones <see cref="Serialization.ParquetSerializer"/> writes. A column is optional unless the reader's schema table says
/// <c>AllowDBNull</c> is false, and a decimal takes the precision and scale the schema table gives (38 and 18 otherwise).
/// </summary>
/// <remarks>
/// The rows go into row groups of <see cref="ParquetDataWriterOptions.RowGroupSize"/> rows, each encoded and written to the stream
/// when it is full, so memory is bounded by one row group whatever the size of the result. Only the current result set is
/// written, and neither the reader nor the stream is closed.
/// </remarks>
public static class ParquetDataWriter
{
    /// <summary>Writes the rest of the rows of the reader to the stream as a Parquet file.</summary>
    /// <exception cref="ArgumentException">The reader has no columns, or the stream is not writable.</exception>
    /// <exception cref="NotSupportedException">A column has a type that cannot be written.</exception>
    /// <exception cref="InvalidOperationException">A column is declared not nullable by the reader, but holds a null.</exception>
    public static void Write(IDataReader source, Stream destination, ParquetDataWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireWritable(destination);
        using var session = new DataWriteSession(source, options);
        destination.Write(session.Staged.Span);
        session.ResetStaged();
        while (source.Read())
        {
            session.Add(source);
            if (session.IsFull)
            {
                session.CloseRowGroup();
                destination.Write(session.Staged.Span);
                session.ResetStaged();
            }
        }

        session.Finish();
        destination.Write(session.Staged.Span);
    }

    /// <summary>Writes the rest of the rows of the reader to the stream as a Parquet file, without blocking while the reader or the stream waits.</summary>
    /// <exception cref="ArgumentException">The reader has no columns, or the stream is not writable.</exception>
    /// <exception cref="NotSupportedException">A column has a type that cannot be written.</exception>
    /// <exception cref="InvalidOperationException">A column is declared not nullable by the reader, but holds a null.</exception>
    public static async Task WriteAsync(DbDataReader source, Stream destination, ParquetDataWriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireWritable(destination);
        using var session = new DataWriteSession(source, options);
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
        while (await source.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            session.Add(source);
            if (session.IsFull)
            {
                session.CloseRowGroup();
                await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
            }
        }

        session.Finish();
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlushAsync(DataWriteSession session, Stream destination, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(session.Staged, cancellationToken).ConfigureAwait(false);
        session.ResetStaged();
    }

    private static void RequireWritable(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(destination));
        }
    }
}
