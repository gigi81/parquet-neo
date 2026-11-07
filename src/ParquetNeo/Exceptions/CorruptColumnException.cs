namespace ParquetNeo.Exceptions;

/// <summary>Thrown when the pages of a column chunk contradict each other or the metadata: counts that do not add up, dictionary or level problems, or an encoding the column's type cannot use.</summary>
public sealed class CorruptColumnException : ParquetException
{
    public CorruptColumnException(string columnName, string detail)
        : base($"Column '{columnName}' is corrupt: {detail}.")
    {
        ColumnName = columnName;
    }

    /// <summary>The name of the column whose data is corrupt.</summary>
    public string ColumnName { get; }
}
