namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>A write-only stream that takes a write in the time a destination of a given speed would (a network, a slow disk), and keeps nothing.</summary>
/// <param name="megabytesPerSecond">The speed; 0 for a destination that takes no time.</param>
internal sealed class ThrottledStream(double megabytesPerSecond) : Stream
{
    public long Written { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Written += buffer.Length;
        if (megabytesPerSecond > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(buffer.Length / (megabytesPerSecond * 1024 * 1024)), cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Written += count;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
