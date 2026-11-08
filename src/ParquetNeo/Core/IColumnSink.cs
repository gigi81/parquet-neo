namespace ParquetNeo.Core;

/// <summary>Where a <see cref="ColumnWriter"/> puts the pages of a column chunk: the file itself, or a buffer that is copied to the file later.</summary>
internal interface IColumnSink
{
    /// <summary>The offset the next byte is written at: in the file, or in the buffer, which is where the chunk will start.</summary>
    long Position { get; }

    void Write(ReadOnlySpan<byte> bytes);

    /// <exception cref="InvalidOperationException">The file can no longer be written.</exception>
    void EnsureUsable();
}
