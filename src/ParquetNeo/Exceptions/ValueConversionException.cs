namespace ParquetNeo.Exceptions;

/// <summary>Thrown when a stored value is valid Parquet but cannot be represented by the .NET type it is read as, such as a date before year 1 or a time beyond a day.</summary>
public sealed class ValueConversionException : ParquetException
{
    public ValueConversionException(string message) : base(message)
    {
    }

    public ValueConversionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
