namespace ParquetNeo.Filtering;

/// <summary>A column is compared with a value; a null is neither less nor equal nor greater.</summary>
internal sealed class ComparisonFilter(string[] path, ComparisonKind kind, object value) : ParquetFilter
{
    internal override FilterOutcome Evaluate(FilterContext context)
    {
        var column = context.Find(path);
        if (column?.Domain is not { } domain || column.Column.MaxRepetitionLevel > 0)
        {
            return FilterOutcome.Some;
        }

        // Whether the type of the value suits the column is a mistake of the caller, which is not hidden by keeping the row group.
        object key = domain.ConvertValue(value, path, out bool ordered);
        if (kind != ComparisonKind.Equal && !ordered)
        {
            return FilterOutcome.Some;
        }

        if (column.IsAllNull || domain.IsNotANumber(key))
        {
            return FilterOutcome.None;
        }

        var statistics = column.Statistics;
        object? low = statistics?.Min is { } min ? domain.Normalize(min) : null;
        object? high = statistics?.Max is { } max ? domain.Normalize(max) : null;

        // Every row can only satisfy the comparison when it has a value that is a number, since a null or a NaN is not compared.
        bool allHaveValues = column.HasNoNulls && (!domain.HasNotANumbers || statistics?.NanCount == 0);
        bool exact = statistics?.IsMinExact == true && statistics.IsMaxExact == true;
        int lowToValue = low is null ? 0 : domain.Compare(low, key);
        int highToValue = high is null ? 0 : domain.Compare(high, key);
        return kind switch
        {
            ComparisonKind.GreaterThan => Decide(none: high is not null && highToValue <= 0, all: low is not null && lowToValue > 0 && allHaveValues),
            ComparisonKind.GreaterThanOrEqual => Decide(none: high is not null && highToValue < 0, all: low is not null && lowToValue >= 0 && allHaveValues),
            ComparisonKind.LessThan => Decide(none: low is not null && lowToValue >= 0, all: high is not null && highToValue < 0 && allHaveValues),
            ComparisonKind.LessThanOrEqual => Decide(none: low is not null && lowToValue > 0, all: high is not null && highToValue <= 0 && allHaveValues),
            _ => Decide(
                none: (low is not null && lowToValue > 0) || (high is not null && highToValue < 0),
                all: low is not null && high is not null && lowToValue == 0 && highToValue == 0 && exact && allHaveValues),
        };
    }

    internal override void CollectColumns(List<IReadOnlyList<string>> paths) => paths.Add(path);

    private static FilterOutcome Decide(bool none, bool all)
    {
        if (none)
        {
            return FilterOutcome.None;
        }

        return all ? FilterOutcome.All : FilterOutcome.Some;
    }
}
