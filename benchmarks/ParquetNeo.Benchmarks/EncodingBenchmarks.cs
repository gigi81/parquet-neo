using System.Buffers;
using BenchmarkDotNet.Attributes;
using ParquetNeo.Encodings;

namespace ParquetNeo.Benchmarks;

/// <summary>Baseline for the bit-packing kernels (1M values per call).</summary>
[MemoryDiagnoser]
public class BitPackingBenchmarks
{
    private const int Count = 1 << 20;
    private uint[] _values = [];
    private uint[] _unpacked = [];
    private byte[] _packed = [];

    [Params(1, 3, 8, 13, 32)]
    public int BitWidth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        ulong limit = 1UL << BitWidth;
        _values = Enumerable.Range(0, Count).Select(_ => (uint)(random.NextInt64() % (long)limit)).ToArray();
        _unpacked = new uint[Count];
        _packed = new byte[Count / 8 * BitWidth];
        BitPacking.Pack(_values, _packed, BitWidth);
    }

    [Benchmark]
    public void Unpack() => BitPacking.Unpack(_packed, _unpacked, BitWidth);

    [Benchmark]
    public void Pack() => BitPacking.Pack(_values, _packed, BitWidth);
}

/// <summary>Baseline for definition-level style data: mostly 1 with a 0 every 7th value, 1M values.</summary>
[MemoryDiagnoser]
public class RleBenchmarks
{
    private const int Count = 1 << 20;
    private int[] _levels = [];
    private int[] _decoded = [];
    private byte[] _encoded = [];
    private readonly ArrayBufferWriter<byte> _output = new(1 << 20);

    [GlobalSetup]
    public void Setup()
    {
        _levels = Enumerable.Range(0, Count).Select(i => i % 7 == 0 ? 0 : 1).ToArray();
        _decoded = new int[Count];
        var buffer = new ArrayBufferWriter<byte>();
        RleBitPackedEncoder.Encode(_levels, 1, buffer);
        _encoded = buffer.WrittenSpan.ToArray();
    }

    [Benchmark]
    public int Decode() => new RleBitPackedDecoder(_encoded, 1).ReadValues(_decoded);

    [Benchmark]
    public int Encode()
    {
        _output.ResetWrittenCount();
        RleBitPackedEncoder.Encode(_levels, 1, _output);
        return _output.WrittenCount;
    }
}

/// <summary>Baseline for PLAIN: 1M fixed-width values, 1M booleans, 100k byte arrays.</summary>
[MemoryDiagnoser]
public class PlainBenchmarks
{
    private const int Count = 1 << 20;
    private byte[] _int32Bytes = [];
    private int[] _int32s = [];
    private byte[] _boolBytes = [];
    private bool[] _bools = [];
    private byte[] _byteArrays = [];
    private ByteRange[] _ranges = [];
    private readonly ArrayBufferWriter<byte> _output = new(1 << 22);

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(3);
        _int32s = new int[Count];
        _int32Bytes = new byte[Count * sizeof(int)];
        random.NextBytes(_int32Bytes);
        _bools = Enumerable.Range(0, Count).Select(_ => random.Next(2) == 1).ToArray();
        _boolBytes = new byte[Count / 8];
        random.NextBytes(_boolBytes);

        var buffer = new ArrayBufferWriter<byte>();
        for (int i = 0; i < 100_000; i++)
        {
            PlainEncoder.WriteByteArray(new byte[random.Next(0, 24)], buffer);
        }

        _byteArrays = buffer.WrittenSpan.ToArray();
        _ranges = new ByteRange[100_000];
    }

    [Benchmark]
    public int ReadInt32s() => PlainDecoder.ReadInt32s(_int32Bytes, _int32s);

    [Benchmark]
    public int ReadBooleans() => PlainDecoder.ReadBooleans(_boolBytes, _bools);

    [Benchmark]
    public int ReadByteArrays() => PlainDecoder.ReadByteArrays(_byteArrays, _ranges);

    [Benchmark]
    public void WriteBooleans()
    {
        _output.ResetWrittenCount();
        PlainEncoder.WriteBooleans(_bools, _output);
    }

    [Benchmark]
    public void WriteInt32s()
    {
        _output.ResetWrittenCount();
        PlainEncoder.WriteInt32s(_int32s, _output);
    }
}
