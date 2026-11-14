namespace ParquetNeo.Filtering;

internal sealed class NotFilter(ParquetFilter inner) : ParquetFilter
{
    internal override ParquetFilter Negate() => inner;

    internal override FilterOutcome Evaluate(FilterContext context) => (FilterOutcome)(2 - (int)inner.Evaluate(context));

    internal override void CollectColumns(List<IReadOnlyList<string>> paths) => inner.CollectColumns(paths);

    public override string ToString() => $"not {inner}";
}
