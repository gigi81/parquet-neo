namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the compressed data of a page is invalid, or does not decompress to the size its page header declares.</summary>
public sealed class CompressionException : ParquetException
{
    /// <summary>Makes the exception with a message.</summary>
    public CompressionException(string message) : base(message)
    {
    }

    /// <summary>Makes the exception with a message and the exception that caused it.</summary>
    public CompressionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
