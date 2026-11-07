using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>Statistics of BOOLEAN values, ordered false before true and stored as one byte.</summary>
internal sealed class BooleanStatistics : StatisticsCollector, IStatisticsSink<bool>
{
    private bool _anyFalse;
    private bool _anyTrue;

    public void Add(ReadOnlySpan<bool> values)
    {
        _anyTrue |= values.Contains(true);
        _anyFalse |= values.Contains(false);
    }

    public override Statistics Build(long nullCount)
    {
        var statistics = new Statistics { NullCount = nullCount };
        if (_anyFalse || _anyTrue)
        {
            statistics.MinValue = [(byte)(_anyFalse ? 0 : 1)];
            statistics.MaxValue = [(byte)(_anyTrue ? 1 : 0)];
            statistics.IsMinValueExact = true;
            statistics.IsMaxValueExact = true;
        }

        return statistics;
    }
}
