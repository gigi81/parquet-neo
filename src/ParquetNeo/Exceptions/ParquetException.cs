namespace ParquetNeo.Exceptions;

/// <summary>
/// The base of the exceptions this library throws for a file it cannot read, so that one <c>catch</c> handles them all.
/// Misuse of the API itself is reported with the standard exceptions instead, such as <see cref="ArgumentException"/>
/// and <see cref="InvalidOperationException"/>.
/// </summary>
public abstract class ParquetException : Exception
{
    protected ParquetException(string message) : base(message)
    {
    }

    protected ParquetException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
