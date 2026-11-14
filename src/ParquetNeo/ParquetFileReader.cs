using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Exceptions;
using ParquetNeo.Filtering;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo;

/// <summary>
/// Reads a Parquet file one row group and one leaf column at a time, with the definition and repetition levels of
/// nested columns. The stream must be readable and seekable, because the metadata sits at the end of the file.
/// </summary>
/// <example>
/// <code>
/// using var reader = new ParquetFileReader(stream);
/// using var column = reader.OpenRowGroup(0).OpenColumn(0);
/// var values = new long[1000];
/// BatchRead read;
/// while ((read = column.ReadBatch(values)).Entries > 0) { /* use values[..read.Entries] */ }
/// </code>
/// Nothing here is thread safe: readers opened from one file share its stream.
/// </example>
public sealed class ParquetFileReader : IDisposable, IAsyncDisposable
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();
    private static readonly byte[] EncryptedMagic = "PARE"u8.ToArray();

    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <param name="input">A readable, seekable stream holding the whole file.</param>
    /// <param name="options">Codecs and safety limits; <c>null</c> for the defaults.</param>
    /// <param name="leaveOpen">Whether to keep <paramref name="input"/> open when the reader is disposed.</param>
    /// <exception cref="ParquetFormatException">The footer is missing or malformed.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted.</exception>
    public ParquetFileReader(Stream input, ParquetReaderOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(input));
        }

        _input = input;
        _leaveOpen = leaveOpen;
        Options = options ?? new ParquetReaderOptions();
        try
        {
            FileLength = input.Length;
            Metadata = ReadFooter();
            Schema = ParquetSchema.FromElements(Metadata.Schema);
            ValidateRowGroups();
        }
        catch
        {
            Release();
            throw;
        }
    }

    public FileMetaData Metadata { get; }

    public ParquetSchema Schema { get; }

    public int RowGroupCount => Metadata.RowGroups.Count;

    /// <summary>The total number of rows in the file, as recorded in the footer.</summary>
    public long RowCount => Metadata.NumRows;

    internal ParquetReaderOptions Options { get; }

    internal long FileLength { get; }

    /// <summary>Opens a row group by its position in the file.</summary>
    public RowGroupReader OpenRowGroup(int index)
    {
        EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RowGroupCount);
        return new RowGroupReader(this, index, Metadata.RowGroups[index]);
    }

    /// <summary>
    /// The positions of the row groups that can hold a row that satisfies a filter, in order: the others are skipped because the
    /// statistics of their columns show that no row does. See <see cref="ParquetFilter"/> for what that means for the rows of the
    /// row groups that are kept.
    /// </summary>
    /// <exception cref="ArgumentException">The filter compares a column with a value of a type that does not suit the column.</exception>
    public IReadOnlyList<int> GetMatchingRowGroups(ParquetFilter filter)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        var matching = new List<int>(RowGroupCount);
        for (int index = 0; index < RowGroupCount; index++)
        {
            if (filter.MayMatch(Schema, Metadata.ColumnOrders, Metadata.RowGroups[index]))
            {
                matching.Add(index);
            }
        }

        return matching;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return _leaveOpen ? ValueTask.CompletedTask : _input.DisposeAsync();
    }

    internal void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void ReadAt(long position, Span<byte> destination)
    {
        EnsureOpen();
        _input.Seek(position, SeekOrigin.Begin);
        try
        {
            _input.ReadExactly(destination);
        }
        catch (EndOfStreamException)
        {
            throw new ParquetFormatException($"The file ends before byte {position + destination.Length}; it is truncated.");
        }
    }

    private FileMetaData ReadFooter()
    {
        // Layout at the end of the file: footer | 4-byte footer length | "PAR1".
        if (FileLength < 12)
        {
            throw new ParquetFormatException($"A Parquet file is at least 12 bytes long, but this one has {FileLength}.");
        }

        Span<byte> head = stackalloc byte[4];
        Span<byte> tail = stackalloc byte[8];
        ReadAt(0, head);
        ReadAt(FileLength - 8, tail);
        CheckMagic(head, "start");
        CheckMagic(tail[4..], "end");

        int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
        if (footerLength <= 0 || footerLength > FileLength - 12)
        {
            throw new ParquetFormatException($"The footer length {footerLength} does not fit in a file of {FileLength} bytes.");
        }

        if (footerLength > Options.MaxFooterSize)
        {
            throw new ParquetFormatException($"The footer is {footerLength} bytes, over the limit of {Options.MaxFooterSize}.");
        }

        var footer = ArrayPool<byte>.Shared.Rent(footerLength);
        try
        {
            ReadAt(FileLength - 8 - footerLength, footer.AsSpan(0, footerLength));
            return FileMetaData.Deserialize(footer.AsSpan(0, footerLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(footer);
        }
    }

    private static void CheckMagic(ReadOnlySpan<byte> magic, string where)
    {
        if (magic.SequenceEqual(EncryptedMagic))
        {
            throw new NotSupportedException("This file is encrypted (it starts or ends with PARE), which is not supported.");
        }

        if (!magic.SequenceEqual(Magic))
        {
            throw new ParquetFormatException($"This is not a Parquet file: the {where} of the file is not the PAR1 marker.");
        }
    }

    private void ValidateRowGroups()
    {
        for (int i = 0; i < Metadata.RowGroups.Count; i++)
        {
            var group = Metadata.RowGroups[i];
            if (group.Columns.Count != Schema.Columns.Count)
            {
                throw new ParquetFormatException(
                    $"Row group {i} has {group.Columns.Count} column chunks but the schema has {Schema.Columns.Count} columns.");
            }

            if (group.NumRows < 0)
            {
                throw new ParquetFormatException($"Row group {i} has a negative row count.");
            }
        }
    }

    private void Release()
    {
        if (!_leaveOpen)
        {
            _input.Dispose();
        }
    }
}
