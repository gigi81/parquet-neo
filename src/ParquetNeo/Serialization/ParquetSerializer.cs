using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>
/// Writes objects as rows of a Parquet file. Each public property becomes a column, named after the property and typed by
/// the convention below, which attributes on the property adjust: <see cref="ParquetColumnAttribute"/>,
/// <see cref="ParquetIgnoreAttribute"/>, <see cref="ParquetDecimalAttribute"/>, <see cref="ParquetTimestampAttribute"/>,
/// <see cref="ParquetTimeAttribute"/> and <see cref="ParquetEncodingAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// A column is optional (it can hold nulls) when the property is a <see cref="Nullable{T}"/>, or a reference type that is
/// not declared non-nullable. Writing a null to a required column is an <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// Supported types: <see cref="bool"/>, the integers of every width, <see cref="float"/>, <see cref="double"/>,
/// <see cref="Half"/>, <see cref="decimal"/> (precision 38 and scale 18 unless set), <see cref="string"/>, <see cref="byte"/>
/// arrays, <see cref="Guid"/>, <see cref="DateTime"/> and <see cref="DateTimeOffset"/> (UTC microseconds unless set),
/// <see cref="DateOnly"/> and <see cref="TimeOnly"/> (microseconds unless set). Others, including nested objects and
/// collections, are a <see cref="NotSupportedException"/> for now.
/// </para>
/// <para>
/// The rows go into row groups of <see cref="ParquetSerializerOptions.RowGroupSize"/> rows, each encoded and written to the
/// stream when it is full. The stream is not closed, and holds a partial file if serializing fails.
/// </para>
/// </remarks>
public static class ParquetSerializer
{
    /// <summary>The schema that rows of <typeparamref name="TRow"/> are written with.</summary>
    /// <exception cref="NotSupportedException">A property has a type that cannot be written as a column.</exception>
    /// <exception cref="InvalidOperationException">The type has no properties to write, two share a column name, or an attribute does not suit its property.</exception>
    public static ParquetSchema GetSchema<TRow>() => new(RowModel<TRow>.Columns.Select(c => c.CreateNode()));

    /// <summary>Writes the rows to the stream as a Parquet file.</summary>
    /// <exception cref="ArgumentException">A row is null, or a null is written to a required column.</exception>
    public static void Serialize<TRow>(IEnumerable<TRow> rows, Stream destination, ParquetSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        RequireWritable(destination);
        using var session = new SerializationSession<TRow>(options);
        destination.Write(session.Staged.Span);
        session.ResetStaged();
        foreach (var row in rows)
        {
            session.Add(row);
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

    /// <summary>Writes the rows to the stream as a Parquet file, without blocking while the stream is written.</summary>
    /// <exception cref="ArgumentException">A row is null, or a null is written to a required column.</exception>
    public static async Task SerializeAsync<TRow>(IEnumerable<TRow> rows, Stream destination, ParquetSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        RequireWritable(destination);
        using var session = new SerializationSession<TRow>(options);
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            session.Add(row);
            if (session.IsFull)
            {
                session.CloseRowGroup();
                await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
            }
        }

        session.Finish();
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the rows to the stream as a Parquet file as they arrive, holding one row group in memory at a time.</summary>
    /// <exception cref="ArgumentException">A row is null, or a null is written to a required column.</exception>
    public static async Task SerializeAsync<TRow>(IAsyncEnumerable<TRow> rows, Stream destination, ParquetSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        RequireWritable(destination);
        using var session = new SerializationSession<TRow>(options);
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            session.Add(row);
            if (session.IsFull)
            {
                session.CloseRowGroup();
                await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
            }
        }

        session.Finish();
        await FlushAsync(session, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlushAsync<TRow>(SerializationSession<TRow> session, Stream destination, CancellationToken cancellationToken)
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
