using ParquetNeo.Encodings;

namespace ParquetNeo.Tests.Encodings;

public class BitPackingTests
{
    [Test]
    public async Task SpecExample_NumbersZeroToSevenAtWidthThree()
    {
        // From the spec: 0..7 at bit width 3 packs, least significant bit first, into 10001000 11000110 11111010.
        uint[] values = [0, 1, 2, 3, 4, 5, 6, 7];
        var packed = new byte[3];

        BitPacking.Pack(values, packed, 3);

        await Assert.That(TestBytes.ToHex(packed)).IsEqualTo("88C6FA");
        await Assert.That(Unpack(packed, 8, 3)).IsEquivalentTo(values);
    }

    [Test]
    public async Task PackAndUnpack_MatchTheBitByBitReference_ForEveryWidth()
    {
        var random = new Random(1234);
        foreach (int groups in new[] { 1, 2, 5, 17 })
        {
            for (int width = 0; width <= 32; width++)
            {
                var values = RandomValues(random, groups * 8, width);
                var expectedBytes = ReferencePack(values, width);

                var packed = new byte[groups * width];
                BitPacking.Pack(values, packed, width);

                await Assert.That(TestBytes.ToHex(packed)).IsEqualTo(TestBytes.ToHex(expectedBytes));

                // An exactly-sized source forces the last groups through the bounds-safe path.
                await Assert.That(Unpack(packed, values.Length, width)).IsEquivalentTo(values);
            }
        }
    }

    [Test]
    public async Task Unpack_GivesTheSameResultWhenTheSourceHasTrailingBytes()
    {
        var random = new Random(99);
        for (int width = 1; width <= 32; width++)
        {
            var values = RandomValues(random, 64, width);
            var packed = ReferencePack(values, width);
            var padded = packed.Concat(Enumerable.Repeat((byte)0xFF, 16)).ToArray();

            await Assert.That(Unpack(padded, values.Length, width)).IsEquivalentTo(values);
        }
    }

    [Test]
    public async Task Pack_DiscardsBitsAboveTheWidth()
    {
        uint[] values = [0xFF, 0, 0, 0, 0, 0, 0, 0];
        var packed = new byte[3];

        BitPacking.Pack(values, packed, 3);

        await Assert.That(TestBytes.ToHex(packed)).IsEqualTo("070000");
    }

    [Test]
    public async Task Width32_RoundTripsTheExtremes()
    {
        uint[] values = [0, 1, uint.MaxValue, 0x80000000, 0x7FFFFFFF, 12345, uint.MaxValue, 0];
        var packed = new byte[32];

        BitPacking.Pack(values, packed, 32);

        await Assert.That(Unpack(packed, 8, 32)).IsEquivalentTo(values);
    }

    [Test]
    public async Task Width0_PacksToNothingAndUnpacksToZeros()
    {
        var destination = Enumerable.Repeat(uint.MaxValue, 16).ToArray();

        BitPacking.Unpack([], destination, 0);
        BitPacking.Pack(new uint[8], [], 0);

        await Assert.That(destination).IsEquivalentTo(new uint[16]);
    }

    [Test]
    public async Task InvalidArguments_AreRejected()
    {
        await Assert.That(() => BitPacking.Unpack(new byte[8], new uint[8], 33)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BitPacking.Unpack(new byte[8], new uint[8], -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BitPacking.Pack(new uint[8], new byte[8], 33)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BitPacking.Unpack(new byte[8], new uint[7], 3)).Throws<ArgumentException>();
        await Assert.That(() => BitPacking.Pack(new uint[9], new byte[16], 3)).Throws<ArgumentException>();
        await Assert.That(() => BitPacking.Unpack(new byte[2], new uint[8], 3)).Throws<ArgumentException>(); // needs 3 bytes
        await Assert.That(() => BitPacking.Pack(new uint[8], new byte[2], 3)).Throws<ArgumentException>();
    }

    private static uint[] Unpack(byte[] packed, int count, int width)
    {
        var values = new uint[count];
        BitPacking.Unpack(packed, values, width);
        return values;
    }

    private static uint[] RandomValues(Random random, int count, int width)
    {
        var values = new uint[count];
        ulong limit = 1UL << width;
        for (int i = 0; i < count; i++)
        {
            values[i] = (uint)(random.NextInt64() % (long)limit);
        }

        return values;
    }

    /// <summary>Independent implementation: bit b of value j goes to bit position j*width + b, LSB first.</summary>
    private static byte[] ReferencePack(uint[] values, int width)
    {
        var bytes = new byte[values.Length * width / 8];
        for (int j = 0; j < values.Length; j++)
        {
            for (int b = 0; b < width; b++)
            {
                if (((values[j] >> b) & 1) != 0)
                {
                    int position = j * width + b;
                    bytes[position >> 3] |= (byte)(1 << (position & 7));
                }
            }
        }

        return bytes;
    }
}
