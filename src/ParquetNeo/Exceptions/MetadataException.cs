namespace ParquetNeo.Exceptions;

/// <summary>Thrown when metadata cannot be decoded: the Thrift data of the footer or of a page header is malformed, or recorded statistics have the wrong size.</summary>
public sealed class MetadataException : ParquetException
{
    public MetadataException(string message) : base(message)
    {
    }

    public MetadataException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
