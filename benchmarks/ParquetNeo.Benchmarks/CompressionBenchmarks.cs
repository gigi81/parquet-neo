using BenchmarkDotNet.Attributes;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Lz4;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;

namespace ParquetNeo.Benchmarks;

/// <summary>Compression and decompression of one 1 MB page, the typical page size, for every codec.</summary>
[MemoryDiagnoser]
public class CompressionBenchmarks
{
    private const int PageSize = 1 << 20;
    private ICodec _codec = null!;
    private byte[] _page = [];
    private byte[] _compressed = [];
    private byte[] _scratch = [];
    private byte[] _restored = [];
    private int _compressedLength;

    [Params("snappy", "gzip", "brotli", "zstd", "lz4raw")]
    public string Codec { get; set; } = "";

    /// <summary>"int64": sequential PLAIN integers; "text": repeated rows with some variation.</summary>
    [Params("int64", "text")]
    public string Data { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _codec = Codec switch
        {
            "snappy" => new SnappyCodec(),
            "gzip" => new GzipCodec(),
            "brotli" => new BrotliCodec(),
            "zstd" => new ZstdCodec(),
            _ => new Lz4RawCodec(),
        };

        _page = new byte[PageSize];
        if (Data == "int64")
        {
            for (int i = 0; i < PageSize / 8; i++)
            {
                BitConverter.TryWriteBytes(_page.AsSpan(i * 8), (long)i * 3);
            }
        }
        else
        {
            var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 40_000).Select(i => $"row {i % 977} value {i * 31 % 101}; ")));
            Array.Copy(text, _page, Math.Min(text.Length, PageSize));
        }

        _scratch = new byte[_codec.GetMaxCompressedLength(PageSize)];
        _compressedLength = _codec.Compress(_page, _scratch);
        _compressed = _scratch[.._compressedLength];
        _restored = new byte[PageSize];
    }

    [Benchmark]
    public int Compress() => _codec.Compress(_page, _scratch);

    [Benchmark]
    public byte[] Decompress()
    {
        _codec.Decompress(_compressed, _restored);
        return _restored;
    }
}
