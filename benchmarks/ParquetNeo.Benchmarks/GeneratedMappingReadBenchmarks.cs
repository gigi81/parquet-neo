using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ParquetNeo.Serialization;

namespace ParquetNeo.Benchmarks;

/// <summary>
/// Reading rows with a mapping generated at build time (<c>[ParquetSerializable]</c>) against the same rows left to reflection, for
/// rows with flat columns only and for rows with objects, lists and a dictionary inside. Both allocate the same rows, so most of what
/// is measured is the garbage collector; the file is the one the generated mapping wrote, which reflection reads as well. Writing is
/// in <see cref="GeneratedMappingWriteBenchmarks"/>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[GcConcurrent(false)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GeneratedMappingReadBenchmarks
{
    private const int Rows = 100_000;
    private byte[] _flatFile = [];
    private byte[] _nestedFile = [];

    [GlobalSetup]
    public void Setup()
    {
        var options = new ParquetSerializerOptions { RowGroupSize = Rows };
        var output = new MemoryStream();
        ParquetSerializer.Serialize(Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillFlat(new GeneratedFlatRow(), i)).ToArray(), output, options);
        _flatFile = output.ToArray();
        output.SetLength(0);
        ParquetSerializer.Serialize(Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillNested(new GeneratedNestedRow(), i)).ToArray(), output, options);
        _nestedFile = output.ToArray();
        GC.Collect();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Flat")]
    public int FlatReflection() => ParquetSerializer.Deserialize<ReflectedFlatRow>(new MemoryStream(_flatFile)).Count;

    [Benchmark, BenchmarkCategory("Flat")]
    public int FlatGenerated() => ParquetSerializer.Deserialize<GeneratedFlatRow>(new MemoryStream(_flatFile)).Count;

    [Benchmark(Baseline = true), BenchmarkCategory("Nested")]
    public int NestedReflection() => ParquetSerializer.Deserialize<ReflectedNestedRow>(new MemoryStream(_nestedFile)).Count;

    [Benchmark, BenchmarkCategory("Nested")]
    public int NestedGenerated() => ParquetSerializer.Deserialize<GeneratedNestedRow>(new MemoryStream(_nestedFile)).Count;
}
