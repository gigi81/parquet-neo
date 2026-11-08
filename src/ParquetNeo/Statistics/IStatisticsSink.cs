namespace ParquetNeo.Statistics;

/// <summary>Takes the values of a batch for a <see cref="StatisticsCollector"/>.</summary>
internal interface IStatisticsSink<T>
{
    void Add(ReadOnlySpan<T> values);
}
