namespace ParquetNeo.Exceptions;

/// <summary>Thrown when a stored value is valid Parquet but cannot be represented by the .NET type it is read as, such as a date before year 1 or a time beyond a day.</summary>
public sealed class ValueConversionException : ParquetException
{
    /// <summary>Makes the exception with a message.</summary>
    public ValueConversionException(string message) : base(message)
    {
    }

    /// <summary>Makes the exception with a message and the exception that caused it.</summary>
    public ValueConversionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
