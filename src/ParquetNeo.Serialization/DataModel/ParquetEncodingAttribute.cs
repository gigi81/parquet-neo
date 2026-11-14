
namespace ParquetNeo.DataModel;

/// <summary>
/// The encoding of the values of a property's column (every column of a collection or dictionary, but not of the properties of an
/// object inside it, which have their own attributes), instead of PLAIN; see
/// <see cref="ParquetWriterOptions.ColumnEncodings"/> for what suits which data. An encoding the column's type cannot
/// use is an <see cref="ArgumentException"/> when serializing. Encodings set in the options win over this one.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetEncodingAttribute(ParquetEncoding encoding) : Attribute
{
    public ParquetEncoding Encoding { get; } = encoding;
}
