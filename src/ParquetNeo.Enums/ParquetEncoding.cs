namespace ParquetNeo;

/// <summary>Value encodings (thrift <c>Encoding</c>).</summary>
public enum ParquetEncoding
{
    Plain = 0,
    /// <summary>Deprecated; <see cref="RleDictionary"/> is used instead.</summary>
    PlainDictionary = 2,
    Rle = 3,
    /// <summary>Deprecated; superseded by <see cref="Rle"/>.</summary>
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    RleDictionary = 8,
    ByteStreamSplit = 9,
    Alp = 10,
}