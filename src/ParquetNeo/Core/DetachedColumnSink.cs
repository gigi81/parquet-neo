using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>
/// Collects the pages of one column chunk in memory, with offsets that count from the start of the chunk, so that columns can be
/// encoded on different threads and copied to the file one after the other (see <see cref="RowGroupWriter"/>).
/// </summary>
internal sealed class DetachedColumnSink : IColumnSink
{
    private readonly ArrayBufferWriter<byte> _bytes = new();

    public long Position => _bytes.WrittenCount;

    /// <summary>What has been written.</summary>
    public ReadOnlySpan<byte> Written => _bytes.WrittenSpan;

    public void Write(ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);

    public void EnsureUsable()
    {
    }
}
