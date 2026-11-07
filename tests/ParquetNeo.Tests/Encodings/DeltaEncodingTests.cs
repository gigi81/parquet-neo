using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Encodings;

public class DeltaEncodingTests
{
    private static byte[] Encode(int[] values)
    {
        var output = new ArrayBufferWriter<byte>();
        DeltaBinaryPackedEncoder.Encode(values, output);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Encode(long[] values)
    {
        var output = new ArrayBufferWriter<byte>();
        DeltaBinaryPackedEncoder.Encode(values, output);
        return output.WrittenSpan.ToArray();
    }

    // ---- DELTA_BINARY_PACKED: the examples of the specification ----

    [Test]
    public async Task ASteadySequence_NeedsNoBitsAtAll()
    {
        // header: 128 values per block, 4 miniblocks, 5 values, first value 1; block: minimum delta 1, four widths of 0
        await Assert.That(TestBytes.ToHex(Encode([1, 2, 3, 4, 5]))).IsEqualTo("80010405" + "02" + "02" + "00000000");
    }

    [Test]
    public async Task TheSpecificationExample_PacksTheRemaindersInTwoBits()
    {
        // 7, 5, 3, 1, 2, 3, 4, 5: deltas -2 -2 -2 1 1 1 1, minimum -2, remainders 0 0 0 3 3 3 3 in 2 bits (padded to 32 values)
        var expected = "80010408" + "0E" + "03" + "02000000" + "C03F000000000000";

        await Assert.That(TestBytes.ToHex(Encode([7, 5, 3, 1, 2, 3, 4, 5]))).IsEqualTo(expected);
    }

    [Test]
    public async Task NoValues_AndOneValue_HaveOnlyAHeader()
    {
        await Assert.That(TestBytes.ToHex(Encode(Array.Empty<int>()))).IsEqualTo("80010400" + "00");
        await Assert.That(TestBytes.ToHex(Encode([-1L]))).IsEqualTo("80010401" + "01");
    }

    // ---- round trips ----

    private static IEnumerable<long[]> Sequences(Random random)
    {
        foreach (int length in new[] { 0, 1, 2, 3, 31, 32, 33, 127, 128, 129, 130, 255, 256, 257, 1000 })
        {
            yield return Enumerable.Range(0, length).Select(i => (long)i).ToArray();
            yield return Enumerable.Range(0, length).Select(i => (long)(length - i) * 1000).ToArray();
            yield return Enumerable.Repeat(77L, length).ToArray();
            yield return Enumerable.Range(0, length).Select(_ => random.NextInt64(-1000, 1000)).ToArray();
            yield return Enumerable.Range(0, length).Select(i => i % 2 == 0 ? long.MinValue : long.MaxValue).ToArray();
            yield return Enumerable.Range(0, length).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).ToArray();
        }
    }

    [Test]
    public async Task Int64Values_RoundTrip()
    {
        var random = new Random(3);
        foreach (var values in Sequences(random))
        {
            var encoded = Encode(values);
            var decoded = new long[values.Length];

            int used = DeltaBinaryPackedDecoder.Decode(encoded, decoded);

            await Assert.That(decoded.AsSpan().SequenceEqual(values)).IsTrue().Because($"{values.Length} values");
            await Assert.That(used).IsEqualTo(encoded.Length);
        }
    }

    [Test]
    public async Task Int32Values_RoundTrip_WithTheirOwnWrapping()
    {
        var random = new Random(4);
        foreach (var wide in Sequences(random))
        {
            var values = wide.Select(v => unchecked((int)v)).ToArray();
            var encoded = Encode(values);
            var decoded = new int[values.Length];

            int used = DeltaBinaryPackedDecoder.Decode(encoded, decoded);

            await Assert.That(decoded.AsSpan().SequenceEqual(values)).IsTrue().Because($"{values.Length} values");
            await Assert.That(used).IsEqualTo(encoded.Length);
        }
    }

    [Test]
    public async Task Int32Remainders_NeverNeedMoreThan32Bits()
    {
        int[] values = [int.MinValue, int.MaxValue, int.MinValue, int.MaxValue];
        var encoded = Encode(values);

        // The widest remainder is 2^32 - 1 (from MinValue to MaxValue and the way back), so 32 bits and no more.
        await Assert.That(DecodeAll(encoded, 4)).IsEquivalentTo(values);
    }

    private static int[] DecodeAll(byte[] encoded, int count)
    {
        var decoded = new int[count];
        DeltaBinaryPackedDecoder.Decode(encoded, decoded);
        return decoded;
    }

    // ---- damaged data ----

    [Test]
    public async Task ADecodeThatFindsADifferentNumberOfValues_Fails()
    {
        var encoded = Encode([1, 2, 3, 4, 5]);

        await Assert.That(() => DeltaBinaryPackedDecoder.Decode(encoded, new int[4])).Throws<EncodingException>();
        await Assert.That(() => DeltaBinaryPackedDecoder.Decode(encoded, new int[6])).Throws<EncodingException>();
    }

    [Test]
    public async Task TruncatedData_Fails_AtEveryLength()
    {
        var encoded = Encode(Enumerable.Range(0, 300).Select(i => i * i % 1013).ToArray());

        for (int length = 0; length < encoded.Length; length++)
        {
            await Assert.That(() => DeltaBinaryPackedDecoder.Decode(encoded.AsSpan(0, length), new int[300])).Throws<EncodingException>().Because($"{length} of {encoded.Length} bytes");
        }
    }

    [Test]
    public async Task ImpossibleHeaders_AreRejected()
    {
        foreach (string header in new[]
        {
            "00" + "04" + "05" + "02",          // block size 0
            "8001" + "00" + "05" + "02",        // no miniblocks
            "8101" + "04" + "05" + "02",        // block size 129: not a multiple of 128
            "8002" + "03" + "05" + "02",        // 256 values over 3 miniblocks
            "8001" + "08" + "05" + "02",        // miniblocks of 16 values: not a multiple of 32
            "80808008" + "04" + "05" + "02",    // a block of 16 million values: otherwise well formed, but over the limit
        })
        {
            var bytes = Convert.FromHexString(header + "02" + "00000000" + new string('0', 64));

            var exception = Assert.Throws<EncodingException>(() => DeltaBinaryPackedDecoder.Decode(bytes, new int[5]));
            await Assert.That(exception.Message).Contains("invalid layout").Because(header);
        }
    }

    [Test]
    public async Task ABitWidthWiderThanTheType_IsRejected()
    {
        // Padded so that a 33-bit miniblock would have all its bytes: only the width itself can be what is wrong.
        var bytes = Encode([7, 5, 3, 1, 2, 3, 4, 5]).Concat(new byte[200]).ToArray();
        bytes[6] = 33; // the first miniblock width (after 5 header bytes and the minimum delta), too wide for INT32

        var exception = Assert.Throws<EncodingException>(() => DeltaBinaryPackedDecoder.Decode(bytes, new int[8]));
        await Assert.That(exception.Message).Contains("bit width");
        await Assert.That(() => DeltaBinaryPackedDecoder.Decode(bytes, new long[8])).ThrowsNothing(); // 33 bits are fine for INT64
    }

    // ---- the pieces ----

    [Test]
    public async Task VariableLengthIntegers_RoundTrip()
    {
        foreach (ulong value in new ulong[] { 0, 1, 127, 128, 16383, 16384, uint.MaxValue, ulong.MaxValue })
        {
            var output = new ArrayBufferWriter<byte>();
            VariableLengthInteger.WriteUnsigned(value, output);
            int position = 0;

            await Assert.That(VariableLengthInteger.ReadUnsigned(output.WrittenSpan, ref position)).IsEqualTo(value);
            await Assert.That(position).IsEqualTo(output.WrittenCount);
        }

        foreach (long value in new[] { 0L, 1, -1, 63, -64, long.MaxValue, long.MinValue })
        {
            var output = new ArrayBufferWriter<byte>();
            VariableLengthInteger.WriteZigZag(value, output);
            int position = 0;

            await Assert.That(VariableLengthInteger.ReadZigZag(output.WrittenSpan, ref position)).IsEqualTo(value);
        }
    }

    [Test]
    public async Task MalformedVariableLengthIntegers_AreRejected()
    {
        int position = 0;
        await Assert.That(() => VariableLengthInteger.ReadUnsigned([0x80], ref position)).Throws<EncodingException>(); // cut off
        position = 0;
        await Assert.That(() => VariableLengthInteger.ReadUnsigned(Enumerable.Repeat((byte)0xFF, 11).ToArray(), ref position)).Throws<EncodingException>();
        position = 0;
        await Assert.That(() => VariableLengthInteger.ReadUnsigned([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02], ref position)).Throws<EncodingException>(); // 65 bits
    }

    [Test]
    public async Task WideBitPacking_RoundTrips_EveryWidth()
    {
        var random = new Random(9);
        for (int width = 0; width <= 64; width++)
        {
            ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            var values = Enumerable.Range(0, 32).Select(_ => (ulong)random.NextInt64() * 2 ^ (ulong)random.NextInt64()).Select(v => v & mask).ToArray();
            var packed = new byte[32 * width / 8];

            WideBitPacking.Pack(values, width, packed);
            var unpacked = new ulong[32];
            WideBitPacking.Unpack(packed, width, unpacked);

            await Assert.That(unpacked).IsEquivalentTo(values).Because($"width {width}");
        }
    }

    // ---- DELTA_LENGTH_BYTE_ARRAY and DELTA_BYTE_ARRAY ----

    private static byte[] Plain(IEnumerable<byte[]> values)
    {
        var output = new ArrayBufferWriter<byte>();
        foreach (var value in values)
        {
            PlainEncoder.WriteByteArray(value, output);
        }

        return output.WrittenSpan.ToArray();
    }

    private static IEnumerable<byte[][]> ByteArrayLists(Random random)
    {
        yield return [];
        yield return [[]];
        yield return [[], [], []];
        yield return [[1, 2, 3], [1, 2, 3], [1, 2, 3, 4], [1, 9], [], [1]];
        yield return Enumerable.Range(0, 500).Select(i => System.Text.Encoding.UTF8.GetBytes($"https://example.com/{i / 7}/{i}")).ToArray();
        yield return Enumerable.Range(0, 300).Select(_ => Enumerable.Range(0, random.Next(0, 40)).Select(_ => (byte)random.Next(256)).ToArray()).ToArray();
    }

    [Test]
    public async Task DeltaLengthByteArrays_RoundTrip()
    {
        foreach (var values in ByteArrayLists(new Random(5)))
        {
            var encoded = new ArrayBufferWriter<byte>();
            DeltaLengthByteArrayEncoder.Encode(Plain(values), values.Length, encoded);
            var decoded = new ArrayBufferWriter<byte>();

            int used = DeltaLengthByteArrayDecoder.Decode(encoded.WrittenSpan, values.Length, decoded);

            await Assert.That(decoded.WrittenSpan.SequenceEqual(Plain(values))).IsTrue().Because($"{values.Length} values");
            await Assert.That(used).IsEqualTo(encoded.WrittenCount);
        }
    }

    [Test]
    public async Task DeltaByteArrays_RoundTrip_AndSharePrefixes()
    {
        foreach (var values in ByteArrayLists(new Random(6)))
        {
            var encoded = new ArrayBufferWriter<byte>();
            DeltaByteArrayEncoder.Encode(Plain(values), values.Length, 0, encoded);
            var decoded = new ArrayBufferWriter<byte>();

            DeltaByteArrayDecoder.Decode(encoded.WrittenSpan, values.Length, 0, 1 << 24, decoded);

            await Assert.That(decoded.WrittenSpan.SequenceEqual(Plain(values))).IsTrue().Because($"{values.Length} values");
        }

        // The URLs share long prefixes, so the encoded form is much smaller than the plain one.
        var urls = Enumerable.Range(0, 500).Select(i => System.Text.Encoding.UTF8.GetBytes($"https://example.com/{i / 7}/{i}")).ToArray();
        var small = new ArrayBufferWriter<byte>();
        DeltaByteArrayEncoder.Encode(Plain(urls), urls.Length, 0, small);
        await Assert.That(small.WrittenCount).IsLessThan(Plain(urls).Length / 2);
    }

    [Test]
    public async Task FixedLengthDeltaByteArrays_RoundTrip()
    {
        var values = Enumerable.Range(0, 400).Select(i => new[] { (byte)(i / 256), (byte)i, (byte)7 }).ToArray();
        var plain = values.SelectMany(v => v).ToArray();
        var encoded = new ArrayBufferWriter<byte>();
        DeltaByteArrayEncoder.Encode(plain, values.Length, 3, encoded);
        var decoded = new ArrayBufferWriter<byte>();

        DeltaByteArrayDecoder.Decode(encoded.WrittenSpan, values.Length, 3, 1 << 20, decoded);

        await Assert.That(decoded.WrittenSpan.SequenceEqual(plain)).IsTrue();
        await Assert.That(() => DeltaByteArrayDecoder.Decode(encoded.WrittenSpan, values.Length, 4, 1 << 20, new ArrayBufferWriter<byte>())).Throws<EncodingException>();
    }

    [Test]
    public async Task PrefixesLongerThanThePreviousValue_AreRejected()
    {
        // First value "ab", second shares 5 bytes with it.
        var prefixes = new ArrayBufferWriter<byte>();
        DeltaBinaryPackedEncoder.Encode(new[] { 0, 5 }, prefixes);
        var suffixes = new ArrayBufferWriter<byte>();
        DeltaLengthByteArrayEncoder.Encode(Plain([[97, 98], [99]]), 2, suffixes);
        var data = prefixes.WrittenSpan.ToArray().Concat(suffixes.WrittenSpan.ToArray()).ToArray();

        await Assert.That(() => DeltaByteArrayDecoder.Decode(data, 2, 0, 1 << 20, new ArrayBufferWriter<byte>())).Throws<EncodingException>();
    }

    [Test]
    public async Task ValuesBuiltFromSharedPrefixes_CannotExceedTheOutputLimit()
    {
        // Each of 1000 values repeats the whole 1 KB of the previous one and adds a byte: a few KB of input, megabytes of output.
        var values = new List<byte[]> { new byte[1000] };
        for (int i = 1; i < 1000; i++)
        {
            values.Add(values[^1].Append((byte)i).ToArray());
        }

        var encoded = new ArrayBufferWriter<byte>();
        DeltaByteArrayEncoder.Encode(Plain(values), values.Count, 0, encoded);

        await Assert.That(encoded.WrittenCount).IsLessThan(10_000);
        await Assert.That(() => DeltaByteArrayDecoder.Decode(encoded.WrittenSpan, values.Count, 0, 100_000, new ArrayBufferWriter<byte>())).Throws<EncodingException>();
        await Assert.That(() => DeltaByteArrayDecoder.Decode(encoded.WrittenSpan, values.Count, 0, 10_000_000, new ArrayBufferWriter<byte>())).ThrowsNothing();
    }

    [Test]
    public async Task DeltaLengthByteArrays_NeedAsManyBytesAsTheLengthsSay()
    {
        var encoded = new ArrayBufferWriter<byte>();
        DeltaLengthByteArrayEncoder.Encode(Plain([[1, 2, 3], [4, 5]]), 2, encoded);

        await Assert.That(() => DeltaLengthByteArrayDecoder.Decode(encoded.WrittenSpan[..^1], 2, new ArrayBufferWriter<byte>())).Throws<EncodingException>();
    }

    // ---- BYTE_STREAM_SPLIT ----

    [Test]
    public async Task ByteStreamSplit_PutsLikeBytesTogether()
    {
        // Two floats 1.0f (00 00 80 3F) and 2.0f (00 00 00 40): first bytes, second bytes, third, fourth.
        var output = new ArrayBufferWriter<byte>();
        ByteStreamSplitEncoder.Encode(new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40 }, 4, output);

        await Assert.That(TestBytes.ToHex(output.WrittenSpan.ToArray())).IsEqualTo("0000" + "0000" + "8000" + "3F40");
    }

    [Test]
    public async Task ByteStreamSplit_RoundTrips_EveryWidth()
    {
        var random = new Random(8);
        foreach (int width in new[] { 1, 2, 3, 4, 8, 16 })
        {
            foreach (int count in new[] { 0, 1, 2, 17, 1000 })
            {
                var plain = new byte[count * width];
                random.NextBytes(plain);
                var split = new ArrayBufferWriter<byte>();
                ByteStreamSplitEncoder.Encode(plain, width, split);
                var back = new byte[plain.Length];

                ByteStreamSplitDecoder.Decode(split.WrittenSpan, count, width, back);

                await Assert.That(back.AsSpan().SequenceEqual(plain)).IsTrue().Because($"{count} values of {width} bytes");
            }
        }
    }

    [Test]
    public async Task ByteStreamSplit_NeedsAllItsBytes()
    {
        await Assert.That(() => ByteStreamSplitDecoder.Decode(new byte[7], 2, 4, new byte[8])).Throws<EncodingException>();
    }
}
