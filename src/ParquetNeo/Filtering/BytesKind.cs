namespace ParquetNeo.Filtering;

/// <summary>What the bytes of a column are.</summary>
internal enum BytesKind
{
    /// <summary>Text (or an enum, or JSON), in UTF-8.</summary>
    Text,

    /// <summary>Bytes of no meaning to the library.</summary>
    Binary,

    /// <summary>A UUID, in the order of RFC 4122.</summary>
    Uuid,
}
