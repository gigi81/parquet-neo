using System.Buffers.Binary;

namespace ParquetNeo.Serialization;

/// <summary>
/// A stand-in for a file of which only some ranges have been read: the footer, and the column chunks of the row group being
/// read. <see cref="ParquetFileReader"/> reads from streams synchronously, so the ranges are first read with asynchronous
/// calls and the reader is then given this stream, which serves them from memory.
/// </summary>
internal sealed class PrefetchedStream(long length) : Stream
{
    private readonly List<Range> _kept = [];
    private readonly List<Range> _loaded = [];

    private readonly record struct Range(long Start, byte[] Data)
    {
        public long End => Start + Data.Length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position { get; set; }

    /// <summary>Reads a range of the source and keeps it until the stream is disposed: the markers and the footer.</summary>
    public Task LoadKeptAsync(Stream source, long start, int count, CancellationToken cancellationToken) =>
        LoadAsync(source, start, count, _kept, cancellationToken);

    /// <summary>Reads a range of the source and keeps it until <see cref="DropLoaded"/>: the column chunks of a row group.</summary>
    public Task LoadChunkAsync(Stream source, long start, int count, CancellationToken cancellationToken) =>
        LoadAsync(source, start, count, _loaded, cancellationToken);

    /// <summary>The number of bytes held for the column chunks that were read ahead.</summary>
    public long LoadedBytes => _loaded.Sum(range => (long)range.Data.Length);

    public void DropLoaded() => _loaded.Clear();

    /// <summary>The little-endian integer at a position of a range that was loaded.</summary>
    public int ReadInt32(long position)
    {
        Span<byte> bytes = stackalloc byte[4];
        Seek(position, SeekOrigin.Begin);
        this.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || Position >= length)
        {
            return 0;
        }

        var range = Find(_kept, Position) ?? Find(_loaded, Position)
            ?? throw new InvalidOperationException($"Byte {Position} of the file was not read ahead; this is a bug of the reader.");
        int offset = (int)(Position - range.Start);
        int count = Math.Min(buffer.Length, range.Data.Length - offset);
        range.Data.AsSpan(offset, count).CopyTo(buffer);
        Position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };
        return Position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static Range? Find(List<Range> ranges, long position)
    {
        foreach (var range in ranges)
        {
            if (position >= range.Start && position < range.End)
            {
                return range;
            }
        }

        return null;
    }

    private static async Task LoadAsync(Stream source, long start, int count, List<Range> into, CancellationToken cancellationToken)
    {
        var data = new byte[count];
        source.Seek(start, SeekOrigin.Begin);
        await source.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        into.Add(new Range(start, data));
    }
}
