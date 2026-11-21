namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the schema of a file is not a well-formed tree of groups and columns.</summary>
public sealed class InvalidSchemaException : ParquetException
{
    /// <summary>Makes the exception with a message.</summary>
    public InvalidSchemaException(string message) : base(message)
    {
    }

    /// <summary>Makes the exception with a message and the exception that caused it.</summary>
    public InvalidSchemaException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
