using System.IO.Compression;
using System.Text;

namespace ParquetNeo.Tests.Compression;

// TEMPORARY: reports how much the runner's zlib expands data. Removed once the cause is known.
public class TemporaryGzipDiagnostics
{
    [Test]
    public void ReportGzipExpansion()
    {
        var report = new StringBuilder();
        report.AppendLine($"runtime {Environment.Version}, cpus {Environment.ProcessorCount}, avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} avx512={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        foreach (var level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            foreach (var (name, data) in CodecTestData.Cases.Concat([new("noise1MB", CodecTestData.Lcg(1 << 20, 99)), new("noise70k", CodecTestData.Lcg(70_000, 70_000))]))
            {
                var ms = new MemoryStream();
                using (var gzip = new GzipWrap(ms, level))
                {
                    gzip.Stream.Write(data);
                }

                long n = data.Length;
                long bound = n + (n >> 8) + 64L;
                report.AppendLine($"{level,-13} {name,-9} n={n,8} compressed={ms.Length,8} bound={bound,8} {(ms.Length > bound ? "EXCEEDS" : "ok")}");
            }
        }

        throw new InvalidOperationException("DIAGNOSTIC REPORT\n" + report);
    }

    private sealed class GzipWrap : IDisposable
    {
        public GzipWrap(MemoryStream target, CompressionLevel level) => Stream = new GZipStream(target, level, leaveOpen: true);

        public GZipStream Stream { get; }

        public void Dispose() => Stream.Dispose();
    }
}
