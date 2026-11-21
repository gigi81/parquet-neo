namespace ParquetNeo;

/// <summary>Value encodings (thrift <c>Encoding</c>).</summary>
public enum ParquetEncoding
{
    /// <summary>The values one after the other, as their type stores them.</summary>
    Plain = 0,
    /// <summary>Deprecated; <see cref="RleDictionary"/> is used instead.</summary>
    PlainDictionary = 2,
    /// <summary>Run length and bit packed hybrid encoding, as used for levels and booleans.</summary>
    Rle = 3,
    /// <summary>Deprecated; superseded by <see cref="Rle"/>.</summary>
    BitPacked = 4,
    /// <summary>The differences between integers, bit packed in miniblocks.</summary>
    DeltaBinaryPacked = 5,
    /// <summary>The lengths of the values, delta encoded, followed by the bytes of all of them.</summary>
    DeltaLengthByteArray = 6,
    /// <summary>Each value as the length it shares with the one before it, and the rest of its bytes.</summary>
    DeltaByteArray = 7,
    /// <summary>Indexes into a dictionary, run length and bit packed.</summary>
    RleDictionary = 8,
    /// <summary>The bytes of floating point values, stored in a stream for each byte position, which compresses better.</summary>
    ByteStreamSplit = 9,
    /// <summary>Adaptive lossless floating point encoding, which is not supported yet.</summary>
    Alp = 10,
}