using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> like <see cref="ArrayBufferWriter{T}"/> whose storage comes from the array pool and goes back
/// to it with <see cref="Dispose"/>, so that the scratch buffers of a writer cost next to nothing to make and to grow, however
/// many files are written.
/// </summary>
internal sealed class PooledBufferWriter<T> : IBufferWriter<T>, IDisposable where T : unmanaged
{
    private const int MinimumCapacity = 256;

    private T[] _buffer = [];
    private int _written;

    public PooledBufferWriter()
    {
    }

    /// <param name="initialCapacity">The room to start with, when it is known about how much will be written, so that the storage does not grow in steps.</param>
    public PooledBufferWriter(int initialCapacity)
    {
        if (initialCapacity > 0)
        {
            _buffer = ArrayPool<T>.Shared.Rent(initialCapacity);
        }
    }

    /// <summary>The number of items written since the last <see cref="ResetWrittenCount"/>.</summary>
    public int WrittenCount => _written;

    public ReadOnlySpan<T> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        _written += count;
    }

    /// <summary>Writes one item; cheaper than <see cref="GetSpan"/> and <see cref="Advance"/> for a stream of single values.</summary>
    public void Append(T item)
    {
        if (_written == _buffer.Length)
        {
            Ensure(1);
        }

        _buffer[_written++] = item;
    }

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Forgets what was written; the storage stays for the next use.</summary>
    public void ResetWrittenCount() => _written = 0;

    /// <summary>Gives the storage back to the pool. The writer can be used again afterwards, starting empty.</summary>
    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = [];
        }

        _written = 0;
    }

    private void Ensure(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        long total = (long)_written + needed;
        long grown = Math.Max(Math.Max(total, (long)_buffer.Length * 2), MinimumCapacity);
        if (grown > Array.MaxLength)
        {
            grown = total <= Array.MaxLength ? Array.MaxLength : throw new InvalidOperationException("The buffer cannot hold that much.");
        }

        var larger = ArrayPool<T>.Shared.Rent((int)grown);
        _buffer.AsSpan(0, _written).CopyTo(larger);
        if (_buffer.Length > 0)
        {
            ArrayPool<T>.Shared.Return(_buffer);
        }

        _buffer = larger;
    }
}
