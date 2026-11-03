namespace ParquetNeo.Encodings;

/// <summary>Where a variable-length value sits inside a page, so values can be read without copying them.</summary>
internal readonly record struct ByteRange(int Offset, int Length);
