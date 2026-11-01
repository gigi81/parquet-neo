namespace ParquetNeo;

/// <summary>Thrown when a Parquet file (or part of one) is malformed.</summary>
public sealed class ParquetFormatException : Exception
{
    public ParquetFormatException(string message) : base(message)
    {
    }
}
