using System.Buffers;
using ParquetNeo.Encodings;

namespace ParquetNeo.Tests.Encodings;

public class RleBitPackedTests
{
    // ---- decoder, hand-written streams ----

    [Test]
    public async Task Decode_RepeatedRun()
    {
        // header = 5 << 1, value 3 in one byte
        await Assert.That(Decode(TestBytes.Hex("0A 03"), 3, 5)).IsEquivalentTo(new[] { 3, 3, 3, 3, 3 });
    }

    [Test]
    public async Task Decode_BitPackedRun_FromTheSpecExample()
    {
        // header = (1 group << 1) | 1, then 0..7 at width 3
        await Assert.That(Decode(TestBytes.Hex("03 88 C6 FA"), 3, 8)).IsEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
    }

    [Test]
    public async Task Decode_MixedRuns()
    {
        var data = TestBytes.Hex("03 88 C6 FA 0A 03");

        await Assert.That(Decode(data, 3, 13)).IsEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 3, 3, 3, 3, 3 });
    }

    [Test]
    public async Task Decode_RepeatedValueUsesAsManyBytesAsTheWidthNeeds()
    {
        // width 9 needs 2 bytes: 300 = 0x012C little endian
        await Assert.That(Decode(TestBytes.Hex("08 2C 01"), 9, 4)).IsEquivalentTo(new[] { 300, 300, 300, 300 });
        await Assert.That(Decode(TestBytes.Hex("04 FF FF FF FF"), 32, 2)).IsEquivalentTo(new[] { -1, -1 }); // 0xFFFFFFFF
        await Assert.That(Decode(TestBytes.Hex("06 12 34 56"), 24, 3)).IsEquivalentTo(new[] { 0x563412, 0x563412, 0x563412 });
    }

    [Test]
    public async Task Decode_Width0_NeedsNoValueBytes()
    {
        await Assert.That(Decode(TestBytes.Hex("0A"), 0, 5)).IsEquivalentTo(new[] { 0, 0, 0, 0, 0 });
        await Assert.That(Decode(TestBytes.Hex("03"), 0, 8)).IsEquivalentTo(new int[8]); // one bit-packed group, zero bytes
    }

    [Test]
    public async Task Decode_ReturnsFewerValuesOnlyWhenTheDataEnds()
    {
        var data = TestBytes.Hex("03 88 C6 FA 0A 03");

        var values = new int[40];
        int count = ReadAll(data, 3, values);

        await Assert.That(count).IsEqualTo(13);
        await Assert.That(ReadAll([], 3, new int[4])).IsEqualTo(0);
    }

    [Test]
    public async Task Decode_ByAnyChunkSize_GivesTheSameValues()
    {
        var data = TestBytes.Hex("03 88 C6 FA 0A 03 05 88 C6 FA 88 C6 FA");
        var expected = Decode(data, 3, 13 + 16);

        foreach (int chunk in new[] { 1, 2, 3, 7, 8, 9, 13, 64 })
        {
            await Assert.That(DecodeChunked(data, 3, expected.Length, chunk)).IsEquivalentTo(expected);
        }
    }

    [Test]
    public async Task Decode_SkipsZeroLengthRuns()
    {
        // RLE run of length 0 (value byte follows), a bit-packed run of 0 groups, then 5 x 3.
        await Assert.That(Decode(TestBytes.Hex("00 07 01 0A 03"), 3, 5)).IsEquivalentTo(new[] { 3, 3, 3, 3, 3 });
    }

    [Test]
    public async Task Decode_ClampsABitPackedRunThatClaimsMoreDataThanExists()
    {
        // 1000 groups declared (varint D1 0F) but only one group of bytes present.
        var data = TestBytes.Hex("D1 0F 88 C6 FA");

        var values = new int[100];
        int count = ReadAll(data, 3, values);

        await Assert.That(count).IsEqualTo(8);
    }

    [Test]
    public async Task Decode_ThrowsOnTruncatedOrOverlongHeaders()
    {
        await Assert.That(() => ReadAll(TestBytes.Hex("0A"), 3, new int[5])).Throws<ParquetFormatException>(); // value missing
        await Assert.That(() => ReadAll(TestBytes.Hex("80"), 3, new int[5])).Throws<ParquetFormatException>(); // header cut off
        await Assert.That(() => ReadAll(TestBytes.Hex("80 80 80 80 80 01"), 3, new int[5])).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task Decode_RejectsAnInvalidBitWidth()
    {
        await Assert.That(() => ReadAll([], 33, new int[1])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ReadAll([], -1, new int[1])).Throws<ArgumentOutOfRangeException>();
    }

    // ---- encoder, golden output ----

    [Test]
    public async Task Encode_SpecExample_IsOneBitPackedRun()
    {
        await Assert.That(TestBytes.ToHex(Encode([0, 1, 2, 3, 4, 5, 6, 7], 3))).IsEqualTo("0388C6FA");
    }

    [Test]
    public async Task Encode_LongRepetition_IsOneRepeatedRun()
    {
        // 100 zeros at width 1: header 100 << 1 = 200 = C8 01, then the value byte.
        await Assert.That(TestBytes.ToHex(Encode(new int[100], 1))).IsEqualTo("C80100");
        await Assert.That(TestBytes.ToHex(Encode(Enumerable.Repeat(4, 8).ToArray(), 3))).IsEqualTo("1004");
    }

    [Test]
    public async Task Encode_ShortRepetition_StaysBitPacked()
    {
        var encoded = Encode(Enumerable.Repeat(4, 7).ToArray(), 3);

        await Assert.That(encoded.Length).IsEqualTo(1 + 3); // one header byte and one padded group
        await Assert.That(encoded[0]).IsEqualTo((byte)0x03);
        await Assert.That(Decode(encoded, 3, 7)).IsEquivalentTo(Enumerable.Repeat(4, 7));
    }

    [Test]
    public async Task Encode_TopsUpTheLiteralGroupBeforeStartingARepeatedRun()
    {
        // 3 distinct values then 20 x 5: five of the fives complete the first group of 8, 15 remain for RLE.
        int[] values = [1, 2, 3, .. Enumerable.Repeat(5, 20)];
        uint[] firstGroup = [1, 2, 3, 5, 5, 5, 5, 5];
        var packed = new byte[3];
        BitPacking.Pack(firstGroup, packed, 3);

        var encoded = Encode(values, 3);

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("03" + TestBytes.ToHex(packed) + "1E05");
    }

    [Test]
    public async Task Encode_RepeatedRunTooShortAfterTopUp_StaysInTheLiterals()
    {
        // 1 odd value then 8 equal: topping up needs 7, leaving only 1, which is not worth a run.
        int[] values = [1, .. Enumerable.Repeat(2, 8)];

        var encoded = Encode(values, 2);

        await Assert.That(encoded.Length).IsEqualTo(1 + 2 * 2); // header + two groups of 2 bytes
        await Assert.That(encoded[0]).IsEqualTo((byte)0x05); // 2 groups, bit-packed
        await Assert.That(Decode(encoded, 2, 9)).IsEquivalentTo(values);
    }

    [Test]
    public async Task Encode_EmptyInput_WritesNothing()
    {
        await Assert.That(Encode([], 5).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Encode_HugeConstantInput_StaysTiny()
    {
        var encoded = Encode(new int[1_000_000], 1);

        await Assert.That(encoded.Length).IsLessThanOrEqualTo(8);
        await Assert.That(DecodeChunked(encoded, 1, 1_000_000, 4096).All(v => v == 0)).IsTrue();
    }

    [Test]
    public async Task Encode_RejectsValuesThatDoNotFit()
    {
        await Assert.That(() => Encode([8], 3)).Throws<ArgumentException>();
        await Assert.That(() => Encode([-1], 3)).Throws<ArgumentException>();
        await Assert.That(() => Encode([1], 0)).Throws<ArgumentException>();
        await Assert.That(() => Encode([1], 33)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Encode_Width32_AcceptsAnyBitPattern()
    {
        int[] values = [-1, int.MinValue, 0, int.MaxValue, 1, -2, 7, 8, 9];

        await Assert.That(Decode(Encode(values, 32), 32, values.Length)).IsEquivalentTo(values);
    }

    // ---- round trips ----

    [Test]
    public async Task RoundTrip_RandomMixesOfRunsAndNoise_AtEveryWidth()
    {
        var random = new Random(2024);
        for (int width = 0; width <= 32; width++)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var values = MixedValues(random, width, random.Next(0, 3000));

                var encoded = Encode(values, width);

                await Assert.That(Decode(encoded, width, values.Length)).IsEquivalentTo(values);
                await Assert.That(DecodeChunked(encoded, width, values.Length, random.Next(1, 200))).IsEquivalentTo(values);
            }
        }
    }

    [Test]
    public async Task RoundTrip_LargeInputSpanningManyBitPackedRuns()
    {
        var random = new Random(5);
        var values = MixedValues(random, 10, 400_000);

        var encoded = Encode(values, 10);

        // SequenceEqual: the collection assertion is far too slow for hundreds of thousands of items.
        await Assert.That(DecodeChunked(encoded, 10, values.Length, 10_000).AsSpan().SequenceEqual(values)).IsTrue();
    }

    [Test]
    public async Task RoundTrip_DefinitionLevelPattern_NullEveryNthValue()
    {
        // The shape of a nullable column's definition levels: mostly 1 with a 0 every 7th row.
        var values = Enumerable.Range(0, 1000).Select(i => i % 7 == 0 ? 0 : 1).ToArray();

        await Assert.That(Decode(Encode(values, 1), 1, 1000)).IsEquivalentTo(values);
    }

    /// <summary>Random segments that are either constant (of random length) or noise.</summary>
    private static int[] MixedValues(Random random, int width, int count)
    {
        var values = new List<int>(count);
        long limit = 1L << width;
        while (values.Count < count)
        {
            int length = Math.Min(count - values.Count, random.Next(1, 40));
            if (random.Next(3) == 0)
            {
                length = Math.Min(count - values.Count, random.Next(1, 400)); // longer runs
                int constant = (int)(uint)(random.NextInt64() % limit);
                values.AddRange(Enumerable.Repeat(constant, length));
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    values.Add((int)(uint)(random.NextInt64() % limit));
                }
            }
        }

        return values.ToArray();
    }

    private static byte[] Encode(int[] values, int width)
    {
        var buffer = new ArrayBufferWriter<byte>();
        RleBitPackedEncoder.Encode(values, width, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static int[] Decode(byte[] data, int width, int count)
    {
        var values = new int[count];
        int read = ReadAll(data, width, values);
        if (read != count)
        {
            throw new InvalidOperationException($"Expected {count} values but the data held {read}.");
        }

        return values;
    }

    private static int ReadAll(byte[] data, int width, int[] destination)
    {
        var decoder = new RleBitPackedDecoder(data, width);
        return decoder.ReadValues(destination);
    }

    private static int[] DecodeChunked(byte[] data, int width, int count, int chunk)
    {
        var decoder = new RleBitPackedDecoder(data, width);
        var values = new int[count];
        int done = 0;
        while (done < count)
        {
            int read = decoder.ReadValues(values.AsSpan(done, Math.Min(chunk, count - done)));
            if (read == 0)
            {
                throw new InvalidOperationException($"The data ended after {done} of {count} values.");
            }

            done += read;
        }

        return values;
    }
}
