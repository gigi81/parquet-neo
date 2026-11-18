namespace ParquetNeo.Geospatial;

/// <summary>Finds the smallest stretch of longitudes, going around the globe from west to east, that holds a set of arcs of longitude; it can cross the antimeridian.</summary>
internal sealed class LongitudeCover
{
    private readonly List<(double Start, double End)> _intervals = [];

    public bool IsEmpty => _intervals.Count == 0;

    /// <summary>Adds the longitudes from <paramref name="start"/> eastwards for <paramref name="length"/> degrees, which are no more than 180; <paramref name="start"/> is from -180 to 180.</summary>
    public void Add(double start, double length)
    {
        double end = start + length;
        if (end <= 180)
        {
            _intervals.Add((start, end));
        }
        else
        {
            // Past the antimeridian: the part up to 180, and the rest from -180.
            _intervals.Add((start, 180));
            _intervals.Add((-180, end - 360));
        }
    }

    /// <summary>The west and east ends of the stretch; the west end is greater than the east one when it crosses the antimeridian.</summary>
    public (double West, double East) Cover()
    {
        _intervals.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var interval in _intervals)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }

        // The stretch that is left out is the largest gap between the intervals, counting the one that goes around the globe from the last to the first.
        var wrapGap = merged[0].Start + 360 - merged[^1].End;
        double largest = wrapGap;
        int after = -1; // the interval before the gap, -1 for the gap that wraps
        for (int i = 0; i + 1 < merged.Count; i++)
        {
            double gap = merged[i + 1].Start - merged[i].End;
            if (gap > largest)
            {
                largest = gap;
                after = i;
            }
        }

        return after < 0 ? (merged[0].Start, merged[^1].End) : (merged[after + 1].Start, merged[after].End);
    }
}
