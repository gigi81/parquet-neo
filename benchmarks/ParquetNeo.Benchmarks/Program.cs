using BenchmarkDotNet.Running;

// Run with: dotnet run -c Release --project benchmarks/ParquetNeo.Benchmarks -- --filter "*"
// Benchmarks are added alongside the features they measure.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
