namespace ParquetNeo.Filtering;

internal sealed class OrFilter(ParquetFilter[] filters) : ParquetFilter
{
    internal override FilterOutcome Evaluate(FilterContext context)
    {
        var outcome = FilterOutcome.None;
        foreach (var filter in filters)
        {
            var each = filter.Evaluate(context);
            if (each == FilterOutcome.All)
            {
                return FilterOutcome.All;
            }

            outcome = (FilterOutcome)Math.Max((int)outcome, (int)each);
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
