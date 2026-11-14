namespace ParquetNeo.Filtering;

/// <summary>How a column is compared with a value.</summary>
internal enum ComparisonKind
{
    Equal,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}
