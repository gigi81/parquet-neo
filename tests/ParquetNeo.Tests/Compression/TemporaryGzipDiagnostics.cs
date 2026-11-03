using System.IO.Compression;
using System.Text;

namespace ParquetNeo.Tests.Compression;

// TEMPORARY: reports how much the runner's zlib expands incompressible data. Removed once the cause is known.
public class TemporaryGzipDiagnostics
{
    [Test]
    public async Task ReportGzipExpansion()
    {
        var report = new StringBuilder();
        report.AppendLine($"runtime {Environment.Version}, os {Environment.OSVersion}, cpus {Environment.ProcessorCount}");
        report.AppendLine($"avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} avx512={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        foreach (var level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            foreach (int n in new[] { 0, 1, 100, 5000, 70_000, 1 << 20 })
            {
                var data = CodecTestData.Lcg(n, (uint)n);
                var ms = new MemoryStream();
                using (var gzip = new GZipStream(ms, level, leaveOpen: true))
                {
                    gzip.Write(data);
                }

                long bound = n + (n >> 8) + 64L;
                report.AppendLine($"{level,-13} n={n,8} compressed={ms.Length,8} overhead={ms.Length - n,7} bound-overhead={bound - n,6} {(ms.Length > bound ? "EXCEEDS" : "ok")}");
            }
        }

        await Assert.That(report.ToString()).IsEqualTo("diagnostic report (this test is expected to fail)");
    }
}
