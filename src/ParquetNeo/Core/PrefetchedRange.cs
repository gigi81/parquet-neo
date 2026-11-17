using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>
/// A stretch of a file that was read into memory with an asynchronous read, so that the synchronous readers, which decode from
/// the stream, find it there and do not block on the stream. It gives its memory back when it is disposed.
/// </summary>
internal sealed class PrefetchedRange(long offset, byte[] buffer, int length, Action<PrefetchedRange> release) : IDisposable
{
    private byte[]? _buffer = buffer;

    public long Offset { get; } = offset;

    public int Length { get; } = length;

    /// <summary>Copies the bytes at <paramref name="position"/> if all of them are in this range.</summary>
    public bool TryCopy(long position, Span<byte> destination)
    {
        if (_buffer is null || position < Offset || position + destination.Length > Offset + Length)
        {
            return false;
        }

        _buffer.AsSpan((int)(position - Offset), destination.Length).CopyTo(destination);
        return true;
    }

    public void Dispose()
    {
        if (_buffer is { } bytes)
        {
            _buffer = null;
            release(this);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}

/// <summary>The ranges prefetched for one call; disposing it releases them all.</summary>
internal sealed class PrefetchLease(List<PrefetchedRange> ranges) : IDisposable
{
    public static PrefetchLease Empty { get; } = new([]);

    public void Dispose()
    {
        foreach (var range in ranges.ToArray())
        {
            range.Dispose();
        }
    }
}
