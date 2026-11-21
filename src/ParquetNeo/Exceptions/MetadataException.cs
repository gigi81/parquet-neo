namespace ParquetNeo.Exceptions;

/// <summary>Thrown when metadata cannot be decoded: the Thrift data of the footer or of a page header is malformed, or recorded statistics have the wrong size.</summary>
public sealed class MetadataException : ParquetException
{
    /// <summary>Makes the exception with a message.</summary>
    public MetadataException(string message) : base(message)
    {
    }

    /// <summary>Makes the exception with a message and the exception that caused it.</summary>
    public MetadataException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
