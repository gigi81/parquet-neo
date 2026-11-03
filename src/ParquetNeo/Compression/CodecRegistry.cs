using System.Collections.Concurrent;
using ParquetNeo.Metadata;

namespace ParquetNeo.Compression;

/// <summary>
/// The codecs available to readers and writers. A new registry holds the built-in ones (uncompressed, Snappy,
/// Gzip, Brotli); codecs from add-on packages such as ParquetNeo.Compression.Zstd are registered explicitly.
/// </summary>
public sealed class CodecRegistry
{
    private readonly ConcurrentDictionary<CompressionCodec, ICodec> _codecs = new();

    public CodecRegistry()
    {
        Register(new UncompressedCodec());
        Register(new SnappyCodec());
        Register(new GzipCodec());
        Register(new BrotliCodec());
    }

    /// <summary>The registry used when none is specified. Register add-on codecs here to use them everywhere.</summary>
    public static CodecRegistry Default { get; } = new();

    /// <summary>Adds a codec, replacing any codec already registered for the same <see cref="ICodec.Id"/>.</summary>
    /// <returns>This registry, for chaining.</returns>
    public CodecRegistry Register(ICodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codecs[codec.Id] = codec;
        return this;
    }

    public bool TryGet(CompressionCodec id, out ICodec codec) => _codecs.TryGetValue(id, out codec!);

    /// <exception cref="NotSupportedException">No codec is registered for <paramref name="id"/>.</exception>
    public ICodec Get(CompressionCodec id)
    {
        if (_codecs.TryGetValue(id, out var codec))
        {
            return codec;
        }

        throw new NotSupportedException(id switch
        {
            CompressionCodec.Zstd => "The ZSTD codec is not registered. Add the ParquetNeo.Compression.Zstd package and register ZstdCodec.",
            CompressionCodec.Lz4Raw => "The LZ4_RAW codec is not registered. Add the ParquetNeo.Compression.Lz4 package and register Lz4RawCodec.",
            CompressionCodec.Lz4 => "The deprecated Hadoop-framed LZ4 codec is not supported; use LZ4_RAW.",
            CompressionCodec.Lzo => "The LZO codec is not supported.",
            _ => $"No codec is registered for compression codec {id}.",
        });
    }
}
