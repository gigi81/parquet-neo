using BenchmarkDotNet.Running;
using ParquetNeo.Benchmarks.Comparison;

// Run with: dotnet run -c Release --project benchmarks/ParquetNeo.Benchmarks -- --filter "*"
// The size of the files each library writes for the comparison dataset: ... -- --sizes
// Benchmarks are added alongside the features they measure.
if (args.Contains("--sizes"))
{
    await ComparisonSizes.PrintAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
