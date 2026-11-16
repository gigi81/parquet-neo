using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;
using ParquetNeo.Schema;

namespace ParquetNeo;

/// <summary>
/// Reads a Parquet file one row group and one leaf column at a time, with the definition and repetition levels of
/// nested columns. The stream must be readable and seekable, because the metadata sits at the end of the file.
/// </summary>
/// <example>
/// <code>
/// using var reader = ParquetFileReader.Open(stream);
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
    private readonly List<PrefetchedRange> _prefetched = [];
    private bool _disposed;

    private ParquetFileReader(Stream input, ParquetReaderOptions options, bool leaveOpen, long fileLength, FileMetaData metadata)
    {
        _input = input;
        _leaveOpen = leaveOpen;
        Options = options;
        FileLength = fileLength;
        Metadata = metadata;
        Schema = ParquetSchema.FromElements(metadata.Schema);
        ValidateRowGroups();
    }

    /// <summary>
    /// Opens a file, reading its footer. Constructors do not use streams, so this is the way to open a file; use <see cref="OpenAsync"/>
    /// to read the footer without blocking.
    /// </summary>
    /// <param name="input">A readable, seekable stream holding the whole file.</param>
    /// <param name="options">Codecs and safety limits; <c>null</c> for the defaults.</param>
    /// <param name="leaveOpen">Whether to keep <paramref name="input"/> open when the reader is disposed.</param>
    /// <exception cref="ParquetFormatException">The footer is missing or malformed.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted.</exception>
    public static ParquetFileReader Open(Stream input, ParquetReaderOptions? options = null, bool leaveOpen = false)
    {
        RequireSeekable(input);
        options ??= new ParquetReaderOptions();
        try
        {
            long length = input.Length;
            return new ParquetFileReader(input, options, leaveOpen, length, ReadFooter(input, length, options));
        }
        catch
        {
            if (!leaveOpen)
            {
                input.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a file like <see cref="Open"/>, reading its footer with asynchronous reads so that no thread waits for the stream.
    /// The rows are read with the methods of <see cref="RowGroupReader"/>; <see cref="RowGroupReader.OpenColumnAsync(int, CancellationToken)"/>
    /// reads a column chunk the same way.
    /// </summary>
    /// <exception cref="ParquetFormatException">The footer is missing or malformed.</exception>
    /// <exception cref="NotSupportedException">The file is encrypted.</exception>
    public static async ValueTask<ParquetFileReader> OpenAsync(Stream input, ParquetReaderOptions? options = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        RequireSeekable(input);
        options ??= new ParquetReaderOptions();
        try
        {
            long length = input.Length;
            var tail = new byte[8];
            var head = new byte[4];
            if (length >= 12)
            {
                await ReadExactlyAtAsync(input, 0, head, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAtAsync(input, length - 8, tail, cancellationToken).ConfigureAwait(false);
            }

            int footerLength = CheckTail(length, head, tail, options);
            var footer = ArrayPool<byte>.Shared.Rent(footerLength);
            try
            {
                await ReadExactlyAtAsync(input, length - 8 - footerLength, footer.AsMemory(0, footerLength), cancellationToken).ConfigureAwait(false);
                return new ParquetFileReader(input, options, leaveOpen, length, FileMetaData.Deserialize(footer.AsSpan(0, footerLength)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footer);
            }
        }
        catch
        {
            if (!leaveOpen)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static void RequireSeekable(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(input));
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
    public IReadOnlyList<int> GetMatchingRowGroups(ParquetFilter filter) => GetMatchingRowGroups(filter, useBloomFilters: true);

    /// <summary>
    /// Finds the row groups that can hold a row that satisfies a filter like <see cref="GetMatchingRowGroups(ParquetFilter)"/>, reading the
    /// bloom filters it needs with asynchronous reads. The statistics rule out row groups first, so only the bloom filters of the
    /// columns of the filter in the row groups that are left are read.
    /// </summary>
    /// <exception cref="ArgumentException">The filter compares a column with a value of a type that does not suit the column.</exception>
    public async ValueTask<IReadOnlyList<int>> GetMatchingRowGroupsAsync(ParquetFilter filter, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        var candidates = GetMatchingRowGroups(filter, useBloomFilters: false);
        var chunks = candidates.SelectMany(index => ColumnChunksOf(Metadata.RowGroups[index], filter.Columns));
        using var lease = await PrefetchAsync(BloomRanges(chunks), cancellationToken).ConfigureAwait(false);
        return GetMatchingRowGroups(filter, useBloomFilters: true);
    }

    /// <param name="useBloomFilters">Whether bloom filters, which are read from the stream, are used as well as the statistics.</param>
    internal IReadOnlyList<int> GetMatchingRowGroups(ParquetFilter filter, bool useBloomFilters)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        var matching = new List<int>(RowGroupCount);
        for (int index = 0; index < RowGroupCount; index++)
        {
            if (filter.MayMatch(Schema, Metadata.ColumnOrders, Metadata.RowGroups[index], useBloomFilters ? ReadBloomFilter : null))
            {
                matching.Add(index);
            }
        }

        return matching;
    }

    /// <summary>More than the header of a bloom filter takes: about a dozen bytes.</summary>
    private const int BloomHeaderPrefetch = 64;

    /// <summary>The bloom filter of a column chunk, or null when it has none or it cannot be read: a filter that cannot be used proves nothing, and is left alone.</summary>
    internal BloomFilter? ReadBloomFilter(ColumnChunk chunk)
    {
        if (chunk.MetaData is not { BloomFilterOffset: { } offset } meta || offset < 4 || offset >= FileLength)
        {
            return null;
        }

        try
        {
            var prefix = new byte[(int)Math.Min(meta.BloomFilterLength ?? BloomHeaderPrefetch, Math.Min(BloomHeaderPrefetch, FileLength - offset))];
            ReadAt(offset, prefix);
            var header = BloomFilterHeader.Deserialize(prefix, out int headerLength);
            long end = offset + headerLength + (long)header.NumBytes;
            if (!header.IsSupported || header.NumBytes <= 0 || header.NumBytes > Options.MaxPageSize || end > FileLength)
            {
                return null;
            }

            var bits = new byte[header.NumBytes];
            ReadAt(offset + headerLength, bits);
            return BloomFilter.FromBytes(bits);
        }
        catch (ParquetException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page index of a column chunk, or null when the file has none or it cannot be used: an index that does not fit its
    /// column chunk proves nothing and is left alone, the rows are then read as if there were none.
    /// </summary>
    internal ColumnPages? ReadColumnPages(ColumnChunk chunk, long rowCount)
    {
        if (chunk.MetaData is not { } meta || chunk.OffsetIndexOffset is not { } offsetsStart || chunk.OffsetIndexLength is not { } offsetsLength)
        {
            return null;
        }

        try
        {
            var offsets = OffsetIndex.Deserialize(ReadIndexBytes(offsetsStart, offsetsLength) ?? []);
            long start = meta.DictionaryPageOffset is > 0 ? meta.DictionaryPageOffset.Value : meta.DataPageOffset;
            long end = start + meta.TotalCompressedSize;
            long previousRow = -1;
            long previousOffset = start - 1;
            foreach (var page in offsets.PageLocations)
            {
                if (page.FirstRowIndex <= previousRow || page.FirstRowIndex >= rowCount || page.Offset <= previousOffset || page.Offset >= end || page.CompressedPageSize <= 0)
                {
                    return null;
                }

                previousRow = page.FirstRowIndex;
                previousOffset = page.Offset;
            }

            if (offsets.PageLocations.Count == 0 || offsets.PageLocations[0].FirstRowIndex != 0)
            {
                return null;
            }

            ColumnIndex? bounds = null;
            if (chunk.ColumnIndexOffset is { } boundsStart && chunk.ColumnIndexLength is { } boundsLength && ReadIndexBytes(boundsStart, boundsLength) is { } bytes)
            {
                bounds = ColumnIndex.Deserialize(bytes);
                int pages = offsets.PageLocations.Count;
                if (bounds.NullPages.Count != pages || bounds.MinValues.Count != pages || bounds.MaxValues.Count != pages || (bounds.NullCounts is { } counts && counts.Count != pages))
                {
                    bounds = null;
                }
            }

            return new ColumnPages(offsets.PageLocations, bounds);
        }
        catch (ParquetException)
        {
            return null;
        }
    }

    private byte[]? ReadIndexBytes(long offset, int length)
    {
        if (offset < 4 || length <= 0 || length > Options.MaxPageSize || offset > FileLength - length)
        {
            return null;
        }

        var bytes = new byte[length];
        ReadAt(offset, bytes);
        return bytes;
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
        ReleasePrefetched();
        return _leaveOpen ? ValueTask.CompletedTask : _input.DisposeAsync();
    }

    internal void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void ReadAt(long position, Span<byte> destination)
    {
        EnsureOpen();
        foreach (var range in _prefetched)
        {
            if (range.TryCopy(position, destination))
            {
                return;
            }
        }

        ReadExactlyAt(_input, position, destination);
    }

    private static void ReadExactlyAt(Stream input, long position, Span<byte> destination)
    {
        input.Seek(position, SeekOrigin.Begin);
        try
        {
            input.ReadExactly(destination);
        }
        catch (EndOfStreamException)
        {
            throw new ParquetFormatException($"The file ends before byte {position + destination.Length}; it is truncated.");
        }
    }

    /// <summary>
    /// Reads stretches of the file into memory with asynchronous reads, so that the reads of the same bytes that follow (the readers decode
    /// from the stream, synchronously) are served from memory. A stretch that does not lie in the file is left out: reading it later
    /// reports the problem. The memory goes back when the lease is disposed.
    /// </summary>
    internal async ValueTask<PrefetchLease> PrefetchAsync(IEnumerable<(long Offset, long Length)> wanted, CancellationToken cancellationToken)
    {
        EnsureOpen();
        var ranges = new List<PrefetchedRange>();
        var lease = new PrefetchLease(ranges);
        try
        {
            foreach (var (offset, length) in wanted)
            {
                if (offset < 4 || length <= 0 || length > int.MaxValue / 2 || offset > FileLength - length)
                {
                    continue;
                }

                var buffer = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    await ReadExactlyAtAsync(_input, offset, buffer.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                var range = new PrefetchedRange(offset, buffer, (int)length, Forget);
                ranges.Add(range);
                _prefetched.Add(range);
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>The bytes of the bloom filters of the chunks that have one, where the file says they are.</summary>
    internal IEnumerable<(long Offset, long Length)> BloomRanges(IEnumerable<ColumnChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.MetaData is { BloomFilterOffset: { } offset, BloomFilterLength: { } length } && length <= Options.MaxPageSize + BloomHeaderPrefetch)
            {
                yield return (offset, length);
            }
        }
    }

    /// <summary>The bytes of the offset index and the column index of the chunks, where the file says they are.</summary>
    internal IEnumerable<(long Offset, long Length)> PageIndexRanges(IEnumerable<ColumnChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.OffsetIndexOffset is { } offsetsStart && chunk.OffsetIndexLength is { } offsetsLength && offsetsLength <= Options.MaxPageSize)
            {
                yield return (offsetsStart, offsetsLength);
            }

            if (chunk.ColumnIndexOffset is { } boundsStart && chunk.ColumnIndexLength is { } boundsLength && boundsLength <= Options.MaxPageSize)
            {
                yield return (boundsStart, boundsLength);
            }
        }
    }

    /// <summary>The chunks of a row group for the columns at these paths; a path the file has no column for is left out.</summary>
    internal IEnumerable<ColumnChunk> ColumnChunksOf(RowGroup group, IEnumerable<IReadOnlyList<string>> paths)
    {
        foreach (var path in paths)
        {
            if (Schema.FindColumn(path) is { } column && column.ColumnIndex < group.Columns.Count)
            {
                yield return group.Columns[column.ColumnIndex];
            }
        }
    }

    private void ReleasePrefetched()
    {
        foreach (var range in _prefetched.ToArray())
        {
            range.Dispose();
        }
    }

    private void Forget(PrefetchedRange range) => _prefetched.Remove(range);

    private static async ValueTask ReadExactlyAtAsync(Stream input, long position, Memory<byte> destination, CancellationToken cancellationToken)
    {
        input.Seek(position, SeekOrigin.Begin);
        try
        {
            await input.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new ParquetFormatException($"The file ends before byte {position + destination.Length}; it is truncated.");
        }
    }

    private static FileMetaData ReadFooter(Stream input, long fileLength, ParquetReaderOptions options)
    {
        // Layout at the end of the file: footer | 4-byte footer length | "PAR1".
        Span<byte> head = stackalloc byte[4];
        Span<byte> tail = stackalloc byte[8];
        if (fileLength >= 12)
        {
            ReadExactlyAt(input, 0, head);
            ReadExactlyAt(input, fileLength - 8, tail);
        }

        int footerLength = CheckTail(fileLength, head, tail, options);
        var footer = ArrayPool<byte>.Shared.Rent(footerLength);
        try
        {
            ReadExactlyAt(input, fileLength - 8 - footerLength, footer.AsSpan(0, footerLength));
            return FileMetaData.Deserialize(footer.AsSpan(0, footerLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(footer);
        }
    }

    /// <summary>Checks the first four and last eight bytes of a file and returns the length of the footer.</summary>
    private static int CheckTail(long fileLength, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, ParquetReaderOptions options)
    {
        if (fileLength < 12)
        {
            throw new ParquetFormatException($"A Parquet file is at least 12 bytes long, but this one has {fileLength}.");
        }

        CheckMagic(head, "start");
        CheckMagic(tail[4..], "end");

        int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
        if (footerLength <= 0 || footerLength > fileLength - 12)
        {
            throw new ParquetFormatException($"The footer length {footerLength} does not fit in a file of {fileLength} bytes.");
        }

        if (footerLength > options.MaxFooterSize)
        {
            throw new ParquetFormatException($"The footer is {footerLength} bytes, over the limit of {options.MaxFooterSize}.");
        }

        return footerLength;
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
        ReleasePrefetched();
        if (!_leaveOpen)
        {
            _input.Dispose();
        }
    }
}
