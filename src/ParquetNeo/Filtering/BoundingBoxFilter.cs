namespace ParquetNeo.Filtering;

/// <summary>A geometry or geography column holds features that may have a point in the rectangle: its row groups are ruled out by their bounding boxes.</summary>
internal sealed class BoundingBoxFilter(string[] path, double xMin, double yMin, double xMax, double yMax) : ParquetFilter
{
    internal override FilterOutcome Evaluate(FilterContext context)
    {
        var column = context.Find(path);
        if (column is null || column.Column.LogicalType?.Kind is not (LogicalTypeKind.Geometry or LogicalTypeKind.Geography))
        {
            return FilterOutcome.Some;
        }

        // A null has no features, so none meets the rectangle.
        if (column.IsAllNull)
        {
            return FilterOutcome.None;
        }

        // The box of the features holds all of them, so a row group whose box does not meet the rectangle has no feature that does.
        return column.Geospatial?.BoundingBox is { } box && !box.Intersects(xMin, yMin, xMax, yMax) ? FilterOutcome.None : FilterOutcome.Some;
    }

    internal override void CollectColumns(List<IReadOnlyList<string>> paths) => paths.Add(path);

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{string.Join('.', path)} intersects [{xMin} {yMin}, {xMax} {yMax}]");
}
