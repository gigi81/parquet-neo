namespace ParquetNeo;

/// <summary>Order of a column's type-defined sort (thrift <c>ColumnOrder</c> union).</summary>
public enum ColumnOrder
{
    /// <summary>A union member this library does not know.</summary>
    Unrecognized = 0,
    /// <summary>The values are ordered by their type: the order of the <see cref="PhysicalType"/> and <c>LogicalType</c> of the column.</summary>
    TypeDefined = 1,
    /// <summary>Floating point values are ordered by the IEEE 754 total order, in which NaN and negative zero have a place.</summary>
    Ieee754TotalOrder = 2,
    /// <summary>The <see cref="PhysicalType.Int96"/> values are timestamps and are ordered as such.</summary>
    Int96Timestamp = 3,
}