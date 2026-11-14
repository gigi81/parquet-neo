using ParquetNeo.Schema;

namespace ParquetNeo.Filtering;

/// <summary>A column is null, or is not.</summary>
internal sealed class NullFilter(string[] path, bool isNull) : ParquetFilter
{
    internal override FilterOutcome Evaluate(FilterContext context)
    {
        var column = context.Find(path);
        if (column is null || !column.IsSimple)
        {
            return FilterOutcome.Some;
        }

        FilterOutcome outcome;
        if (column.HasNoNulls)
        {
            outcome = FilterOutcome.None;
        }
        else if (column.IsAllNull)
        {
            outcome = FilterOutcome.All;
        }
        else
        {
            outcome = FilterOutcome.Some;
        }

        // "Is null" is that outcome, and "is not null" is its opposite.
        return isNull ? outcome : (FilterOutcome)(2 - (int)outcome);
    }

    internal override void CollectColumns(List<IReadOnlyList<string>> paths) => paths.Add(path);
}
