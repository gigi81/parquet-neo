namespace ParquetNeo;

/// <summary>Order of a column's type-defined sort (thrift <c>ColumnOrder</c> union).</summary>
public enum ColumnOrder
{
    /// <summary>A union member this library does not know.</summary>
    Unrecognized = 0,
    TypeDefined = 1,
    Ieee754TotalOrder = 2,
    Int96Timestamp = 3,
}