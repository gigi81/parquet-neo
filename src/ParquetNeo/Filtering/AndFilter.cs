namespace ParquetNeo.Filtering;

internal sealed class AndFilter(ParquetFilter[] filters) : ParquetFilter
{
    internal override FilterOutcome Evaluate(FilterContext context)
    {
        var outcome = FilterOutcome.All;
        foreach (var filter in filters)
        {
            var each = filter.Evaluate(context);
            if (each == FilterOutcome.None)
            {
                return FilterOutcome.None;
            }

            outcome = (FilterOutcome)Math.Min((int)outcome, (int)each);
        }

        return outcome;
    }

    internal override void CollectColumns(List<IReadOnlyList<string>> paths)
    {
        foreach (var filter in filters)
        {
            filter.CollectColumns(paths);
        }
    }
}
