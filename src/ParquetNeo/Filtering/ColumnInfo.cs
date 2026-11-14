using ParquetNeo.Schema;
using ParquetNeo.Statistics;

namespace ParquetNeo.Filtering;

/// <summary>What a filter needs to know of one column of a row group: how its values compare, and what the statistics of the column chunk say.</summary>
/// <param name="Domain">How values of the column compare, or null when the column has no ordering the statistics can be trusted for.</param>
/// <param name="Statistics">What the writer recorded for the column chunk, or null when nothing usable.</param>
/// <param name="NumValues">The number of values, nulls included, in the column chunk.</param>
internal sealed record ColumnInfo(PrimitiveNode Column, ValueDomain? Domain, ColumnStatistics? Statistics, long NumValues)
{
    /// <summary>
    /// Whether the column has one entry per row, which is null or a value: not in a collection, and below no optional object. Only
    /// then do the null count and the number of values say how many rows are null.
    /// </summary>
    public bool IsSimple => Column.MaxRepetitionLevel == 0 && Column.MaxDefinitionLevel == (Column.Repetition == FieldRepetitionType.Optional ? 1 : 0);

    /// <summary>Whether it is known that no row is null.</summary>
    public bool HasNoNulls => IsSimple && (Column.Repetition == FieldRepetitionType.Required || Statistics?.NullCount == 0);

    /// <summary>Whether it is known that every row is null.</summary>
    public bool IsAllNull => IsSimple && Statistics?.NullCount is { } nulls && nulls == NumValues && NumValues > 0;
}
