namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the layout of a Parquet file is wrong: it is truncated, lacks the PAR1 markers, or its footer and row groups do not fit in it.</summary>
public sealed class ParquetFormatException : ParquetException
{
    public ParquetFormatException(string message) : base(message)
    {
    }

    public ParquetFormatException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
