using System.IO.Compression;
using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>The codecs of the library itself, in the configurations the tests run: the add-on codecs are tested in the projects of their packages.</summary>
internal static class BuiltInCodecs
{
    public static IReadOnlyList<(string Name, ICodec Codec)> All { get; } =
    [
        ("uncompressed", new UncompressedCodec()),
        ("gzip", new GzipCodec()),
        ("gzip-fastest", new GzipCodec(CompressionLevel.Fastest)),
        ("gzip-smallest", new GzipCodec(CompressionLevel.SmallestSize)),
        ("brotli", new BrotliCodec()),
        ("brotli-0", new BrotliCodec(0)),
        ("brotli-9", new BrotliCodec(9)),
    ];

    /// <summary>The ones that compress.</summary>
    public static IEnumerable<(string Name, ICodec Codec)> Compressing => All.Where(c => c.Name != "uncompressed");

    public static (string Name, ICodec Codec) Named(string name) => All.First(c => c.Name == name);
}
