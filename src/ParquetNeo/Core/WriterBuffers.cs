namespace ParquetNeo.Core;

/// <summary>Reusable buffers shared by the column writers of one file (only one column is written at a time).</summary>
internal sealed class WriterBuffers : IDisposable
{
    public PooledBufferWriter<byte> Values { get; } = new();

    public PooledBufferWriter<int> Levels { get; } = new();

    public PooledBufferWriter<int> RepetitionLevels { get; } = new();

    /// <summary>Dictionary indexes of the values buffered for the current page.</summary>
    public PooledBufferWriter<int> Indices { get; } = new();

    public PooledBufferWriter<bool> Booleans { get; } = new();

    public PooledBufferWriter<byte> LevelBytes { get; } = new();

    public PooledBufferWriter<byte> RepetitionBytes { get; } = new();

    /// <summary>The repetition and definition level sections of a page of a repeated column, back to back.</summary>
    public PooledBufferWriter<byte> LevelBlock { get; } = new();

    /// <summary>The values of a page converted from PLAIN to the encoding chosen for the column.</summary>
    public PooledBufferWriter<byte> Encoded { get; } = new();

    public PooledBufferWriter<byte> Header { get; } = new();

    public void Reset()
    {
        Values.ResetWrittenCount();
        Levels.ResetWrittenCount();
        Indices.ResetWrittenCount();
        Booleans.ResetWrittenCount();
        LevelBytes.ResetWrittenCount();
        RepetitionLevels.ResetWrittenCount();
        RepetitionBytes.ResetWrittenCount();
        LevelBlock.ResetWrittenCount();
        Encoded.ResetWrittenCount();
    }

    /// <summary>Gives the storage of all the buffers back to the pool.</summary>
    public void Dispose()
    {
        Values.Dispose();
        Levels.Dispose();
        RepetitionLevels.Dispose();
        Indices.Dispose();
        Booleans.Dispose();
        LevelBytes.Dispose();
        RepetitionBytes.Dispose();
        LevelBlock.Dispose();
        Encoded.Dispose();
        Header.Dispose();
    }
}
