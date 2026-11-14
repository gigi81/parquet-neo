using System.IO.Compression;
using ParquetNeo.Encodings;

namespace ParquetNeo.Tests.Encodings;

public class Crc32Tests
{
    private static uint Naive(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    [Test]
    public async Task TheCheckValueOfTheStandard_IsTheOneEveryImplementationGives()
    {
        await Assert.That(Crc32.Compute("123456789"u8)).IsEqualTo(0xCBF43926u);
        await Assert.That(Crc32.Compute([])).IsEqualTo(0u);
        await Assert.That(Crc32.Compute("a"u8)).IsEqualTo(0xE8B7BE43u);
    }

    [Test]
    public async Task EveryLength_GivesWhatTheBitByBitDefinitionGives()
    {
        var random = new Random(5);
        var data = new byte[600];
        random.NextBytes(data);

        for (int length = 0; length <= data.Length; length++)
        {
            await Assert.That(Crc32.Compute(data.AsSpan(0, length))).IsEqualTo(Naive(data.AsSpan(0, length)));
        }
    }

    [Test]
    public async Task ChecksummingInPieces_IsChecksummingTheWhole()
    {
        var random = new Random(6);
        var data = new byte[1000];
        random.NextBytes(data);
        uint whole = Crc32.Compute(data);

        for (int first = 0; first <= data.Length; first += 37)
        {
            uint pieces = Crc32.Append(Crc32.Compute(data.AsSpan(0, first)), data.AsSpan(first));
            await Assert.That(pieces).IsEqualTo(whole);
        }
    }

    [Test]
    public async Task TheChecksumOfGzip_IsTheSame()
    {
        // The trailer of a gzip stream holds the CRC-32 of what was compressed, worked out by the runtime.
        var data = new byte[5000];
        new Random(7).NextBytes(data);
        var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        byte[] bytes = stream.ToArray();
        uint trailer = BitConverter.ToUInt32(bytes, bytes.Length - 8);

        await Assert.That(Crc32.Compute(data)).IsEqualTo(trailer);
    }
}
