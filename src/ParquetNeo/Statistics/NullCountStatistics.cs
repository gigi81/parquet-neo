namespace ParquetNeo.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = Metadata.Statistics;

/// <summary>For columns whose sort order is undefined or not supported yet: only the null count is recorded.</summary>
internal sealed class NullCountStatistics : StatisticsCollector
{
    public override Statistics Build(long nullCount) => new() { NullCount = nullCount };
}
