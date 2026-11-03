using System.Buffers;
using System.Text;
using ParquetNeo.Encodings;

namespace ParquetNeo.Tests.Encodings;

public class PlainEncodingTests
{
    [Test]
    public async Task Int32_IsFourBytesLittleEndian()
    {
        var encoded = Write(w => PlainEncoder.WriteInt32s([1, -1, 0x01020304], w));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("01000000" + "FFFFFFFF" + "04030201");
        var decoded = new int[3];
        await Assert.That(PlainDecoder.ReadInt32s(encoded, decoded)).IsEqualTo(12);
        await Assert.That(decoded).IsEquivalentTo(new[] { 1, -1, 0x01020304 });
    }

    [Test]
    public async Task Int64_IsEightBytesLittleEndian()
    {
        var encoded = Write(w => PlainEncoder.WriteInt64s([1L, -2L, long.MaxValue], w));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("0100000000000000" + "FEFFFFFFFFFFFFFF" + "FFFFFFFFFFFFFF7F");
        var decoded = new long[3];
        await Assert.That(PlainDecoder.ReadInt64s(encoded, decoded)).IsEqualTo(24);
        await Assert.That(decoded).IsEquivalentTo(new[] { 1L, -2L, long.MaxValue });
    }

    [Test]
    public async Task Floats_AreIeeeLittleEndian()
    {
        var singles = Write(w => PlainEncoder.WriteSingles([1.5f, float.NegativeInfinity], w));
        var doubles = Write(w => PlainEncoder.WriteDoubles([1.5, -0.0], w));

        await Assert.That(TestBytes.ToHex(singles)).IsEqualTo("0000C03F" + "000080FF");
        await Assert.That(TestBytes.ToHex(doubles)).IsEqualTo("000000000000F83F" + "0000000000000080");

        var decodedSingles = new float[2];
        var decodedDoubles = new double[2];
        PlainDecoder.ReadSingles(singles, decodedSingles);
        PlainDecoder.ReadDoubles(doubles, decodedDoubles);
        await Assert.That(decodedSingles).IsEquivalentTo(new[] { 1.5f, float.NegativeInfinity });
        await Assert.That(double.IsNegative(decodedDoubles[1])).IsTrue(); // negative zero keeps its sign
    }

    [Test]
    public async Task FloatingPoint_PreservesNaNPayloadBits()
    {
        double nan = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_1234);
        var encoded = Write(w => PlainEncoder.WriteDoubles([nan], w));

        var decoded = new double[1];
        PlainDecoder.ReadDoubles(encoded, decoded);

        await Assert.That(BitConverter.DoubleToInt64Bits(decoded[0])).IsEqualTo(0x7FF8_0000_0000_1234L);
    }

    [Test]
    public async Task Int96_IsNanosOfDayThenJulianDay()
    {
        var value = new Int96(nanosOfDay: 0x0102030405060708, julianDay: 2_458_850);

        var encoded = Write(w => PlainEncoder.WriteInt96s([value], w));
        var decoded = new Int96[1];
        PlainDecoder.ReadInt96s(encoded, decoded);

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("0807060504030201" + "E2842500");
        await Assert.That(decoded[0].NanosOfDay).IsEqualTo(0x0102030405060708L);
        await Assert.That(decoded[0].JulianDay).IsEqualTo(2_458_850);
    }

    [Test]
    public async Task Booleans_PackLeastSignificantBitFirst()
    {
        // true false true true false false false false | true  -> 0b00001101, 0b00000001
        bool[] values = [true, false, true, true, false, false, false, false, true];

        var encoded = Write(w => PlainEncoder.WriteBooleans(values, w));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("0D01");
        var decoded = new bool[9];
        await Assert.That(PlainDecoder.ReadBooleans(encoded, decoded)).IsEqualTo(2);
        await Assert.That(decoded).IsEquivalentTo(values);
    }

    [Test]
    public async Task Booleans_MatchABitByBitReference_AtEveryLengthAroundTheVectorWidth()
    {
        var random = new Random(7);
        for (int length = 0; length <= 70; length++)
        {
            var values = Enumerable.Range(0, length).Select(_ => random.Next(2) == 1).ToArray();
            var expected = new byte[(length + 7) / 8];
            for (int i = 0; i < length; i++)
            {
                if (values[i])
                {
                    expected[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            var encoded = Write(w => PlainEncoder.WriteBooleans(values, w));
            var decoded = new bool[length];
            PlainDecoder.ReadBooleans(expected, decoded);

            await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo(TestBytes.ToHex(expected));
            await Assert.That(decoded).IsEquivalentTo(values);
        }
    }

    [Test]
    public async Task Booleans_EncodingOverwritesDirtyOutputBuffers()
    {
        var dirty = new DirtyBufferWriter(0xFF);
        PlainEncoder.WriteBooleans(new bool[20], dirty); // all false

        await Assert.That(TestBytes.ToHex(dirty.Written)).IsEqualTo("000000");
    }

    [Test]
    public async Task ByteArrays_AreLengthPrefixed_AndDecodeToRangesWithoutCopying()
    {
        var encoded = Write(w =>
        {
            PlainEncoder.WriteByteArray("ab"u8, w);
            PlainEncoder.WriteByteArray([], w);
            PlainEncoder.WriteByteArray("xyz"u8, w);
        });

        var ranges = new ByteRange[3];
        int consumed = PlainDecoder.ReadByteArrays(encoded, ranges);

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("02000000" + "6162" + "00000000" + "03000000" + "78797A");
        await Assert.That(consumed).IsEqualTo(encoded.Length);
        await Assert.That(ranges).IsEquivalentTo(new[] { new ByteRange(4, 2), new ByteRange(10, 0), new ByteRange(14, 3) });
        await Assert.That(Encoding.UTF8.GetString(encoded.AsSpan(ranges[2].Offset, ranges[2].Length))).IsEqualTo("xyz");
    }

    [Test]
    public async Task ByteArrays_StopAfterTheRequestedCount()
    {
        var encoded = Write(w =>
        {
            PlainEncoder.WriteByteArray("a"u8, w);
            PlainEncoder.WriteByteArray("b"u8, w);
        });

        var ranges = new ByteRange[1];
        int consumed = PlainDecoder.ReadByteArrays(encoded, ranges);

        await Assert.That(consumed).IsEqualTo(5); // only the first value
    }

    [Test]
    public async Task FixedLengthByteArrays_AreBackToBack()
    {
        var encoded = Write(w =>
        {
            PlainEncoder.WriteFixedLengthByteArray([1, 2, 3], 3, w);
            PlainEncoder.WriteFixedLengthByteArray([4, 5, 6], 3, w);
        });

        string valuesHex = TestBytes.ToHex(PlainDecoder.ReadFixedLengthByteArrays(encoded, 2, 3));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("010203040506");
        await Assert.That(valuesHex).IsEqualTo("010203040506");
        await Assert.That(() => PlainEncoder.WriteFixedLengthByteArray([1, 2], 3, new ArrayBufferWriter<byte>()))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task EmptyInputs_ConsumeAndProduceNothing()
    {
        await Assert.That(Write(w => PlainEncoder.WriteInt32s([], w)).Length).IsEqualTo(0);
        await Assert.That(Write(w => PlainEncoder.WriteBooleans([], w)).Length).IsEqualTo(0);
        await Assert.That(PlainDecoder.ReadInt64s([], [])).IsEqualTo(0);
        await Assert.That(PlainDecoder.ReadBooleans([], [])).IsEqualTo(0);
        await Assert.That(PlainDecoder.ReadByteArrays([], [])).IsEqualTo(0);
    }

    [Test]
    public async Task RandomValues_RoundTrip()
    {
        var random = new Random(11);
        var ints = Enumerable.Range(0, 1000).Select(_ => random.Next(int.MinValue, int.MaxValue)).ToArray();
        var longs = Enumerable.Range(0, 1000).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).ToArray();
        var doubles = Enumerable.Range(0, 1000).Select(_ => random.NextDouble() * 1e9 - 5e8).ToArray();
        var singles = Enumerable.Range(0, 1000).Select(_ => (float)(random.NextDouble() * 1e3)).ToArray();

        var decodedInts = new int[1000];
        var decodedLongs = new long[1000];
        var decodedDoubles = new double[1000];
        var decodedSingles = new float[1000];
        PlainDecoder.ReadInt32s(Write(w => PlainEncoder.WriteInt32s(ints, w)), decodedInts);
        PlainDecoder.ReadInt64s(Write(w => PlainEncoder.WriteInt64s(longs, w)), decodedLongs);
        PlainDecoder.ReadDoubles(Write(w => PlainEncoder.WriteDoubles(doubles, w)), decodedDoubles);
        PlainDecoder.ReadSingles(Write(w => PlainEncoder.WriteSingles(singles, w)), decodedSingles);

        await Assert.That(decodedInts.AsSpan().SequenceEqual(ints)).IsTrue();
        await Assert.That(decodedLongs.AsSpan().SequenceEqual(longs)).IsTrue();
        await Assert.That(decodedDoubles.AsSpan().SequenceEqual(doubles)).IsTrue();
        await Assert.That(decodedSingles.AsSpan().SequenceEqual(singles)).IsTrue();
    }

    [Test]
    public async Task TooShortData_ThrowsParquetFormatException()
    {
        await Assert.That(() => PlainDecoder.ReadInt32s(new byte[7], new int[2])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadInt64s(new byte[15], new long[2])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadSingles(new byte[3], new float[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadDoubles(new byte[7], new double[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadInt96s(new byte[23], new Int96[2])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadBooleans(new byte[1], new bool[9])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadFixedLengthByteArrays(new byte[5], 2, 3)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task MalformedByteArrays_ThrowParquetFormatException()
    {
        var truncatedPrefix = TestBytes.Hex("020000");
        var lengthBeyondData = TestBytes.Hex("05000000 6162");
        var negativeLength = TestBytes.Hex("FFFFFFFF 6162");
        var hugeLength = TestBytes.Hex("FFFFFF7F 6162");
        var missingSecondValue = TestBytes.Hex("01000000 61");

        await Assert.That(() => PlainDecoder.ReadByteArrays(truncatedPrefix, new ByteRange[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadByteArrays(lengthBeyondData, new ByteRange[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadByteArrays(negativeLength, new ByteRange[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadByteArrays(hugeLength, new ByteRange[1])).Throws<ParquetFormatException>();
        await Assert.That(() => PlainDecoder.ReadByteArrays(missingSecondValue, new ByteRange[2])).Throws<ParquetFormatException>();
    }

    private static byte[] Write(Action<IBufferWriter<byte>> action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        action(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A buffer writer whose spans start filled with a marker, to catch encoders that assume zeroed memory.</summary>
    private sealed class DirtyBufferWriter(byte fill) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        private int _written;

        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Grow(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Grow(sizeHint).Span;

        private Memory<byte> Grow(int sizeHint)
        {
            int needed = _written + Math.Max(sizeHint, 1);
            if (_buffer.Length < needed)
            {
                var bigger = new byte[needed];
                Array.Fill(bigger, fill);
                _buffer.AsSpan(0, _written).CopyTo(bigger);
                _buffer = bigger;
            }

            return _buffer.AsMemory(_written);
        }
    }
}
