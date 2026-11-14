namespace ParquetNeo.Filtering;

internal sealed class BooleanDomain : ValueDomain
{
    public override object? Normalize(object statistic) => statistic is bool ? statistic : null;

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        return value is bool ? value : throw Mismatch(value, path, "booleans");
    }

    public override int Compare(object left, object right) => ((bool)left).CompareTo((bool)right);
}
