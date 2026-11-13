using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ParquetNeo.Serialization;

namespace ParquetNeo.Benchmarks;

/// <summary>
/// Writing rows with a mapping generated at build time (<c>[ParquetSerializable]</c>) against the same rows left to reflection (the
/// mapping is found and compiled when the type is first used, which the warm-up takes out of what is measured), for rows with flat
/// columns only and for rows with objects, lists and a dictionary inside. What the generated mapping is for is trimming and Native
/// AOT; this checks that it costs nothing at run time. Reading is in <see cref="GeneratedMappingReadBenchmarks"/>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[GcConcurrent(false)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GeneratedMappingWriteBenchmarks
{
    private const int Rows = 100_000;
    private readonly ParquetSerializerOptions _options = new() { RowGroupSize = Rows };
    private GeneratedFlatRow[] _generatedFlat = [];
    private ReflectedFlatRow[] _reflectedFlat = [];
    private GeneratedNestedRow[] _generatedNested = [];
    private ReflectedNestedRow[] _reflectedNested = [];
    private MemoryStream _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _generatedFlat = Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillFlat(new GeneratedFlatRow(), i)).ToArray();
        _reflectedFlat = Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillFlat(new ReflectedFlatRow(), i)).ToArray();
        _generatedNested = Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillNested(new GeneratedNestedRow(), i)).ToArray();
        _reflectedNested = Enumerable.Range(0, Rows).Select(i => GeneratedRowData.FillNested(new ReflectedNestedRow(), i)).ToArray();
        _output = new MemoryStream(16 * 1024 * 1024);
    }

    [GlobalCleanup]
    public void Cleanup() => _output.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Flat")]
    public long FlatReflection() => Write(_reflectedFlat);

    [Benchmark, BenchmarkCategory("Flat")]
    public long FlatGenerated() => Write(_generatedFlat);

    [Benchmark(Baseline = true), BenchmarkCategory("Nested")]
    public long NestedReflection() => Write(_reflectedNested);

    [Benchmark, BenchmarkCategory("Nested")]
    public long NestedGenerated() => Write(_generatedNested);

    private long Write<TRow>(TRow[] rows)
    {
        _output.SetLength(0);
        ParquetSerializer.Serialize(rows, _output, _options);
        return _output.Length;
    }
}
