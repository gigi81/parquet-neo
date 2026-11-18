namespace ParquetNeo.Tests;

/// <summary>
/// A stream over another that only takes asynchronous reads and writes: a synchronous one is a failure, so that a test can show that
/// a code path does not block on the stream. Seeking and the length are fine.
/// </summary>
public sealed class AsyncOnlyStream(Stream inner) : Stream
{
    public int AsyncReads { get; private set; }

    public int AsyncWrites { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Flush() => throw Blocked();

    public override int Read(byte[] buffer, int offset, int count) => throw Blocked();

    public override void Write(byte[] buffer, int offset, int count) => throw Blocked();

    public override int Read(Span<byte> buffer) => throw Blocked();

    public override void Write(ReadOnlySpan<byte> buffer) => throw Blocked();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        AsyncReads++;
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        AsyncWrites++;
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        await inner.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private static NotSupportedException Blocked() => new("A synchronous read, write or flush: the code path blocks on the stream.");
}
