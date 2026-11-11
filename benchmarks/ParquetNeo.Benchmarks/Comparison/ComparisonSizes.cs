
namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>
/// Prints the size of the file each library writes for the comparison dataset (<c>dotnet run -c Release -- --sizes</c>): the
/// timings of <see cref="WriteComparison"/> only mean something next to the work done, and Parquet.Net, for one, writes every
/// column PLAIN, which is quick and makes its files about twice the size of those of the libraries that use a dictionary.
/// </summary>
internal static class ComparisonSizes
{
    public static async Task PrintAsync()
    {
        foreach (var compression in new[] { CompressionCodec.Uncompressed, CompressionCodec.Snappy })
        {
            var benchmark = new WriteComparison { Compression = compression };
            benchmark.Setup();
            try
            {
                Console.WriteLine($"{compression}, 1,000,000 rows, 5 columns");
                Print("ParquetNeo, columns", benchmark.ParquetNeoColumns());
                Print("ParquetNeo, columns, 4 threads", benchmark.ParquetNeoColumnsInParallel());
                Print("ParquetNeo, columns, no dictionary", benchmark.ParquetNeoColumnsPlain());
                Print("ParquetNeo, columns, adaptive dictionaries", benchmark.ParquetNeoColumnsAdaptive());
                Print("ParquetNeo, serializer", benchmark.ParquetNeoSerializer());
                Print("Parquet.Net, columns", await benchmark.ParquetNetColumns());
                Print("Parquet.Net, serializer", await benchmark.ParquetNetSerializer());
                Print("ParquetSharp, columns", benchmark.ParquetSharpColumns());
                Console.WriteLine();
            }
            finally
            {
                benchmark.Cleanup();
            }
        }
    }

    private static void Print(string name, long bytes) => Console.WriteLine($"  {name,-36} {bytes / 1024.0 / 1024.0,7:F1} MB");
}
