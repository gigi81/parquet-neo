using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>Collects the distinct values of a column chunk, in order of first appearance, for dictionary encoding.</summary>
internal abstract class DictionaryBuilder
{
    /// <summary>The number of distinct values so far.</summary>
    public abstract int Count { get; }

    /// <summary>The size in bytes the dictionary page body would have (the entries encoded PLAIN).</summary>
    public abstract long EncodedSize { get; }

    /// <summary>Writes the entries, PLAIN encoded and in index order, which is the body of the dictionary page.</summary>
    public abstract void WritePlain(IBufferWriter<byte> output);
}
