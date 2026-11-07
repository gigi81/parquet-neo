namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the compressed data of a page is invalid, or does not decompress to the size its page header declares.</summary>
public sealed class CompressionException : ParquetException
{
    public CompressionException(string message) : base(message)
    {
    }

    public CompressionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
