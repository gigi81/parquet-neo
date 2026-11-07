namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the schema of a file is not a well-formed tree of groups and columns.</summary>
public sealed class InvalidSchemaException : ParquetException
{
    public InvalidSchemaException(string message) : base(message)
    {
    }

    public InvalidSchemaException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
