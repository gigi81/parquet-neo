using ParquetNeo.Core;

namespace ParquetNeo.Serialization;

/// <summary>Settings for reading rows with <see cref="ParquetSerializer"/>.</summary>
public sealed record ParquetDeserializerOptions
{
    /// <summary>The codecs to decompress with, and the limits that protect against corrupt files; the defaults when not set.</summary>
    public ParquetReaderOptions Reader { get; init; } = new();
}
