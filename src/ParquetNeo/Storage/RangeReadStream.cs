namespace ParquetNeo.Storage;

/// <summary>
/// A stream that reads an object of an object storage, which has a size and gives out ranges of its bytes, as a seekable, read-only
/// stream: the base of the streams of the storage packages. A range is read for what is asked, and no more than a block of
/// <see cref="RangeReadStreamOptions.BlockSize"/> for a small read, so that <see cref="ParquetFileReader"/>, which reads a footer and then pages
/// at the positions it knows, makes few requests.
/// </summary>
/// <remarks>Not safe for use from several threads at once. The synchronous methods wait for the asynchronous ones.</remarks>
public abstract class RangeReadStream : Stream
{
    private readonly int _blockSize;
    private readonly int _cachedBlocks;
    private readonly long _tailStart;
    private byte[]? _tail;
    private readonly List<(long Index, byte[] Data)> _blocks = [];
    private long _position;

    /// <param name="length">The size of the object.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative, or the options are not a block of at least a byte and a block kept.</exception>
    protected RangeReadStream(long length, RangeReadStreamOptions? options = null)
    {
        options ??= new RangeReadStreamOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BlockSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CachedBlocks, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.TailSize);
        Length = length;
        _tailStart = options.TailSize == 0 ? length : Math.Max(0, length - options.TailSize);
        _blockSize = options.BlockSize;
        _cachedBlocks = options.CachedBlocks;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <summary>
    /// Reads bytes of the object from <paramref name="offset"/>, as many as fit in <paramref name="destination"/>, which is never longer than
    /// what is left of the object, and returns how many were read. Fewer is allowed, and the rest is asked for again; 0 means the object ended,
    /// which a stream of the size it said does not do.
    /// </summary>
    protected abstract ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        // The asynchronous read needs memory that can be held across an await; the bytes are copied from it.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = ReadAsync(rented.AsMemory(0, buffer.Length), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= Length)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, Length - _position);
        int read;
        if (_position >= _tailStart)
        {
            read = await ReadFromTailAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            read = count >= _blockSize
                ? await ReadDirectAsync(buffer[..count], cancellationToken).ConfigureAwait(false)
                : await ReadFromBlockAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        }

        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The position is before the start of the stream.");
        }

        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("The stream is read only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read only.");

    /// <summary>A read inside the last bytes of the object: from the tail, which the first such read takes from the storage in one request.</summary>
    private async ValueTask<int> ReadFromTailAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_tail is null)
        {
            var tail = new byte[(int)(Length - _tailStart)];
            await ReadFullyAsync(_tailStart, tail, cancellationToken).ConfigureAwait(false);
            _tail = tail;
        }

        _tail.AsMemory((int)(_position - _tailStart), buffer.Length).CopyTo(buffer);
        return buffer.Length;
    }

    private async ValueTask ReadFullyAsync(long start, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int read = await ReadRangeAsync(start + done, destination[done..], cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw EndedEarly(start + done);
            }

            done += read;
        }
    }

    /// <summary>A request of its own for the whole of a large read.</summary>
    private async ValueTask<int> ReadDirectAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await ReadFullyAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    /// <summary>A small read: from the block it is in, which is taken from the storage when it is not one of the last read.</summary>
    private async ValueTask<int> ReadFromBlockAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        long index = _position / _blockSize;
        var data = await BlockAsync(index, cancellationToken).ConfigureAwait(false);
        int offset = (int)(_position - (index * _blockSize));
        int count = Math.Min(buffer.Length, data.Length - offset);
        data.AsMemory(offset, count).CopyTo(buffer);
        return count;
    }

    private async ValueTask<byte[]> BlockAsync(long index, CancellationToken cancellationToken)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Index == index)
            {
                var found = _blocks[i];
                _blocks.RemoveAt(i);
                _blocks.Add(found);
                return found.Data;
            }
        }

        long start = index * _blockSize;
        var data = new byte[(int)Math.Min(_blockSize, Length - start)];
        await ReadFullyAsync(start, data, cancellationToken).ConfigureAwait(false);

        if (_blocks.Count >= _cachedBlocks)
        {
            _blocks.RemoveAt(0);
        }

        _blocks.Add((index, data));
        return data;
    }

    private IOException EndedEarly(long position) =>
        new($"The object ended at byte {position}, but it is {Length} bytes: it changed while it was read, or the storage gave less than was asked for.");
}
