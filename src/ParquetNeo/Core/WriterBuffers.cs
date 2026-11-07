using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>Reusable buffers shared by the column writers of one file (only one column is written at a time).</summary>
internal sealed class WriterBuffers
{
    public ArrayBufferWriter<byte> Values { get; } = new();

    public ArrayBufferWriter<int> Levels { get; } = new();

    /// <summary>Dictionary indexes of the values buffered for the current page.</summary>
    public ArrayBufferWriter<int> Indices { get; } = new();

    public ArrayBufferWriter<bool> Booleans { get; } = new();

    public ArrayBufferWriter<byte> LevelBytes { get; } = new();

    public ArrayBufferWriter<byte> Header { get; } = new();

    public void Reset()
    {
        Values.ResetWrittenCount();
        Levels.ResetWrittenCount();
        Indices.ResetWrittenCount();
        Booleans.ResetWrittenCount();
        LevelBytes.ResetWrittenCount();
    }
}
