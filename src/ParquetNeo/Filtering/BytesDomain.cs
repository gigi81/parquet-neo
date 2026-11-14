namespace ParquetNeo.Filtering;

/// <summary>Text and binary values, as the bytes the statistics hold, compared as unsigned bytes.</summary>
internal sealed class BytesDomain(BytesKind kind) : ValueDomain
{
    public override object? Normalize(object statistic) => statistic as byte[];

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        switch (kind)
        {
            case BytesKind.Text:
                if (value is string text)
                {
                    return System.Text.Encoding.UTF8.GetBytes(text);
                }

                // An enum is stored by the name of its value, whose order is not the order of the values.
                ordered = false;
                return value is Enum named ? System.Text.Encoding.UTF8.GetBytes(named.ToString()) : throw Mismatch(value, path, "text");
            case BytesKind.Uuid:
                // The order of the bytes of a UUID is not the order of a Guid, so only equality is decided.
                ordered = false;
                return value is Guid guid ? guid.ToByteArray(bigEndian: true) : throw Mismatch(value, path, "UUIDs");
            default:
                return value switch
                {
                    byte[] bytes => bytes,
                    string converted => System.Text.Encoding.UTF8.GetBytes(converted),
                    _ => throw Mismatch(value, path, "binary values"),
                };
        }
    }

    public override int Compare(object left, object right) => ((byte[])left).AsSpan().SequenceCompareTo((byte[])right);
}
