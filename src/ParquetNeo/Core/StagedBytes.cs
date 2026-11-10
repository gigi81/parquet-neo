namespace ParquetNeo.Core;

/// <summary>
/// Bytes of a file that were encoded into memory and are waiting to be written to a stream; the memory goes back to the pool when
/// they are disposed, which must not happen before the write is over.
/// </summary>
internal sealed class StagedBytes(PooledBufferWriter<byte> storage) : IDisposable
{
    public ReadOnlyMemory<byte> Memory => storage.WrittenMemory;

    public void Dispose() => storage.Dispose();
}
