using System.Buffers;
using BenchmarkDotNet.Attributes;
using ParquetNeo.Encodings;

namespace ParquetNeo.Benchmarks;

/// <summary>Encoding and decoding 1M integers with DELTA_BINARY_PACKED (steady, with small jitter) and BYTE_STREAM_SPLIT (8-byte values).</summary>
[MemoryDiagnoser]
public class DeltaBenchmarks
{
    private const int Count = 1 << 20;
    private long[] _values = [];
    private long[] _decoded = [];
    private byte[] _delta = [];
    private byte[] _split = [];
    private byte[] _plain = [];
    private readonly ArrayBufferWriter<byte> _output = new();

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        _values = new long[Count];
        long value = 1_000_000;
        for (int i = 0; i < Count; i++)
        {
            value += 1000 + random.Next(-50, 50);
            _values[i] = value;
        }

        _decoded = new long[Count];
        var delta = new ArrayBufferWriter<byte>();
        DeltaBinaryPackedEncoder.Encode(_values, delta);
        _delta = delta.WrittenSpan.ToArray();
        _plain = new byte[Count * sizeof(long)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(_values.AsSpan()).CopyTo(_plain);
        var split = new ArrayBufferWriter<byte>();
        ByteStreamSplitEncoder.Encode(_plain, sizeof(long), split);
        _split = split.WrittenSpan.ToArray();
    }

    [Benchmark]
    public int EncodeDelta()
    {
        _output.ResetWrittenCount();
        DeltaBinaryPackedEncoder.Encode(_values, _output);
        return _output.WrittenCount;
    }

    [Benchmark]
    public int DecodeDelta() => DeltaBinaryPackedDecoder.Decode(_delta, _decoded);

    [Benchmark]
    public int EncodeByteStreamSplit()
    {
        _output.ResetWrittenCount();
        ByteStreamSplitEncoder.Encode(_plain, sizeof(long), _output);
        return _output.WrittenCount;
    }

    [Benchmark]
    public void DecodeByteStreamSplit() => ByteStreamSplitDecoder.Decode(_split, Count, sizeof(long), _plain);
}
