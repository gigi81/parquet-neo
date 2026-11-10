using BenchmarkDotNet.Attributes;
using ParquetNeo.Serialization;

namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>
/// <c>SerializeAsync</c> of the comparison dataset in four row groups to a destination of a given speed: the next row group is
/// encoded while the one before is written, so the time approaches that of the slower of encoding and writing instead of the sum.
/// A speed of 0 is a destination that takes no time (a memory stream).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class AsyncWriteComparison
{
    private ComparisonData _data = null!;
    private readonly ParquetSerializerOptions _options = new() { RowGroupSize = ComparisonData.Rows / 4 };

    [Params(0, 400, 100)]
    public int MegabytesPerSecond { get; set; }

    [GlobalSetup]
    public void Setup() => _data = new ComparisonData();

    [Benchmark(Description = "ParquetNeo, SerializeAsync, 4 row groups")]
    public async Task<long> SerializeAsync()
    {
        var destination = new ThrottledStream(MegabytesPerSecond);
        await ParquetSerializer.SerializeAsync(_data.Orders, destination, _options);
        return destination.Written;
    }
}
