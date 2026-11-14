using ParquetNeo.Core;
using ParquetNeo.Metadata.Headers;

namespace ParquetNeo.Tests.Core;

public class BloomFilterTests
{
    [Test]
    public async Task TheSizeIsAPowerOfTwo_BetweenOneBlockAndTheMostTheSettingsAllow()
    {
        await Assert.That(BloomFilter.OptimalByteCount(1, 0.05, 1 << 20)).IsEqualTo(32);
        await Assert.That(BloomFilter.OptimalByteCount(0, 0.05, 1 << 20)).IsEqualTo(32);

        int previous = 0;
        foreach (long distinct in new long[] { 10, 1000, 100_000, 10_000_000 })
        {
            int bytes = BloomFilter.OptimalByteCount(distinct, 0.01, 1 << 30);
            await Assert.That(int.IsPow2(bytes)).IsTrue();
            await Assert.That(bytes).IsGreaterThanOrEqualTo(previous);
            await Assert.That(bytes * 8.0 / distinct).IsGreaterThan(8);
            previous = bytes;
        }

        await Assert.That(BloomFilter.OptimalByteCount(10_000_000, 0.01, 1 << 16)).IsEqualTo(1 << 16);
        await Assert.That(BloomFilter.OptimalByteCount(100, 0.01, 8)).IsEqualTo(32);
        await Assert.That(BloomFilter.OptimalByteCount(1000, 0.0001, 1 << 20)).IsGreaterThan(BloomFilter.OptimalByteCount(1000, 0.1, 1 << 20));
    }

    [Test]
    public async Task EveryValueInserted_IsFound_AndFewOthersAre()
    {
        var random = new Random(11);
        var inserted = Enumerable.Range(0, 10_000).Select(_ => random.NextInt64()).ToList();
        var filter = BloomFilter.Create(BloomFilter.OptimalByteCount(inserted.Count, 0.01, 1 << 24));
        foreach (long value in inserted)
        {
            filter.Insert(BloomFilter.HashOf(BitConverter.GetBytes(value)));
        }

        await Assert.That(inserted.All(value => filter.MightContain(BloomFilter.HashOf(BitConverter.GetBytes(value))))).IsTrue();

        int falsePositives = Enumerable.Range(0, 20_000).Count(_ => filter.MightContain(BloomFilter.HashOf(BitConverter.GetBytes(random.NextInt64()))));

        // A rate of 0.01 asked for: well under 0.03 of the 20,000.
        await Assert.That(falsePositives).IsLessThan(600);
    }

    [Test]
    public async Task TheBitsAreTheSame_AfterTheyAreCopiedOutAndRead()
    {
        var filter = BloomFilter.Create(256);
        for (int i = 0; i < 100; i++)
        {
            filter.Insert(BloomFilter.HashOf(BitConverter.GetBytes(i)));
        }

        var bits = new byte[filter.ByteCount];
        filter.CopyTo(bits);
        var read = BloomFilter.FromBytes(bits)!;

        await Assert.That(read.ByteCount).IsEqualTo(256);
        await Assert.That(Enumerable.Range(0, 100).All(i => read.MightContain(BloomFilter.HashOf(BitConverter.GetBytes(i))))).IsTrue();
        await Assert.That(Enumerable.Range(1000, 1000).Count(i => read.MightContain(BloomFilter.HashOf(BitConverter.GetBytes(i))) != filter.MightContain(BloomFilter.HashOf(BitConverter.GetBytes(i))))).IsEqualTo(0);
        await Assert.That(BloomFilter.FromBytes(new byte[31])).IsNull();
        await Assert.That(BloomFilter.FromBytes([])).IsNull();
    }

    [Test]
    public async Task OneInsertedValue_SetsOneBitInEachWordOfOneBlock()
    {
        var filter = BloomFilter.Create(32);
        filter.Insert(0x1234_5678_9ABC_DEF0UL);
        var bits = new byte[32];
        filter.CopyTo(bits);

        for (int word = 0; word < 8; word++)
        {
            await Assert.That(System.Numerics.BitOperations.PopCount(BitConverter.ToUInt32(bits, word * 4))).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheHeader_IsWhatTheFormatSays()
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        new BloomFilterHeader { NumBytes = 32 }.Serialize(output);

        // numBytes 32; the algorithm, the hash and the compression, each a union whose first member is set to an empty struct.
        await Assert.That(Convert.ToHexString(output.WrittenSpan)).IsEqualTo("154" + "0" + "1C1C0000" + "1C1C0000" + "1C1C0000" + "00");

        var read = BloomFilterHeader.Deserialize(output.WrittenSpan, out int length);
        await Assert.That(read.NumBytes).IsEqualTo(32);
        await Assert.That(read.IsSupported).IsTrue();
        await Assert.That(length).IsEqualTo(output.WrittenCount);
    }

    [Test]
    public async Task AHeaderOfAnotherAlgorithmHashOrCompression_IsNotSupported()
    {
        // numBytes 32; the algorithm's second member (unknown to this library) is set.
        byte[] other = Convert.FromHexString("1540" + "1C" + "2C00" + "00" + "1C1C0000" + "1C1C0000" + "00");

        var header = BloomFilterHeader.Deserialize(other, out _);

        await Assert.That(header.NumBytes).IsEqualTo(32);
        await Assert.That(header.IsSupported).IsFalse();
    }
}
