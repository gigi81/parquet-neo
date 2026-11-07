namespace ParquetNeo.Exceptions;

/// <summary>Thrown when encoded values or levels cannot be decoded: the data is too short, or is not valid for its encoding (PLAIN, RLE, delta, byte-stream-split).</summary>
public sealed class EncodingException : ParquetException
{
    public EncodingException(string message) : base(message)
    {
    }

    public EncodingException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
