namespace ParquetNeo.Core;

/// <summary>
/// A stream that keeps what is written to it in memory taken from the pool, for a writer that is handed the bytes to write to the
/// real stream asynchronously: <see cref="Take"/> hands over what was written so far and starts a new buffer, so that the next row
/// group can be encoded while the bytes of the last one are on their way. Each buffer is made as big as the one before, since row
/// groups are about the same size.
/// </summary>
internal sealed class StagingStream : Stream
{
    private PooledBufferWriter<byte> _current = new();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _current.WrittenCount;

    public override long Position
    {
        get => _current.WrittenCount;
        set => throw new NotSupportedException();
    }

    /// <summary>The bytes written since the last call, and an empty buffer for the ones that follow.</summary>
    public StagedBytes Take()
    {
        var taken = new StagedBytes(_current);
        _current = new PooledBufferWriter<byte>(_current.WrittenCount);
        return taken;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        buffer.CopyTo(_current.GetSpan(buffer.Length));
        _current.Advance(buffer.Length);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current.Dispose();
        }

        base.Dispose(disposing);
    }
}
