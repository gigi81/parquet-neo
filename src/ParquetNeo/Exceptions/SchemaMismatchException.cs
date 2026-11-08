namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the schema of a file does not fit the type it is read into: a column that is missing, or stored as a type the property cannot hold.</summary>
public sealed class SchemaMismatchException : ParquetException
{
    public SchemaMismatchException(string message) : base(message)
    {
    }

    public SchemaMismatchException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
