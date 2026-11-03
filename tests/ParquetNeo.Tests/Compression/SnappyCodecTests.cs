using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>Hand-built Snappy streams, one per element type of the format, and the ways they can be wrong.</summary>
public class SnappyCodecTests
{
    private static readonly SnappyCodec Snappy = new();

    [Test]
    public async Task Literal_ShortAndLong()
    {
        // preamble 5, literal tag (5-1)<<2 = 0x10, "hello"
        await Assert.That(Decode("05 10 68656C6C6F", 5)).IsEqualTo("hello");

        // A 100-byte literal needs the 1-byte length form: tag 60<<2 = 0xF0 then length-1 = 99.
        var hundred = new string('x', 100);
        var stream = TestBytes.Hex("64 F0 63").Concat(System.Text.Encoding.ASCII.GetBytes(hundred)).ToArray();
        await Assert.That(Decode(stream, 100)).IsEqualTo(hundred);

        // 300 bytes: tag 61<<2 = 0xF4, then length-1 = 299 in two bytes (2B 01).
        var threeHundred = new string('y', 300);
        var longStream = TestBytes.Hex("AC 02 F4 2B 01").Concat(System.Text.Encoding.ASCII.GetBytes(threeHundred)).ToArray();
        await Assert.That(Decode(longStream, 300)).IsEqualTo(threeHundred);
    }

    [Test]
    public async Task CopyWithOneByteOffset()
    {
        // "abcd" then copy length 4 from 4 back: tag 01 | (4-4)<<2 | (offset>>8)<<5 = 0x01, offset byte 04.
        await Assert.That(Decode("08 0C 61626364 01 04", 8)).IsEqualTo("abcdabcd");
    }

    [Test]
    public async Task CopyWithTwoByteOffset()
    {
        // "abcdefgh" then copy 6 bytes from 8 back: tag 02 | (6-1)<<2 = 0x16, offset 0008.
        await Assert.That(Decode("0E 1C 6162636465666768 16 0800", 14)).IsEqualTo("abcdefghabcdef");
    }

    [Test]
    public async Task CopyWithFourByteOffset()
    {
        // The same copy using the 4-byte form: tag 03 | (6-1)<<2 = 0x17, offset 00000008.
        await Assert.That(Decode("0E 1C 6162636465666768 17 08000000", 14)).IsEqualTo("abcdefghabcdef");
    }

    [Test]
    public async Task OverlappingCopy_RepeatsThePattern()
    {
        // "ab" then copy 10 bytes from 2 back: tag 02 | (10-1)<<2 = 0x26, offset 0002 (the source overlaps the output).
        await Assert.That(Decode("0C 04 6162 26 0200", 12)).IsEqualTo("ababababab" + "ab");
        // "a" then copy 9 from 1 back is a run: tag 02 | (9-1)<<2 = 0x22, offset 0001.
        await Assert.That(Decode("0A 00 61 22 0100", 10)).IsEqualTo("aaaaaaaaaa");
    }

    [Test]
    public async Task EmptyOutput_IsJustThePreamble()
    {
        await Assert.That(Decode("00", 0)).IsEqualTo(string.Empty);
        await Assert.That(TestBytes.ToHex(CodecTestData.Compress(Snappy, []))).IsEqualTo("00");
    }

    [Test]
    public async Task TheEncoder_EmitsTheSameBytesAsTheReferenceImplementation_ForSimpleInputs()
    {
        // Reference outputs produced by libsnappy through pyarrow.
        await Assert.That(TestBytes.ToHex(CodecTestData.Compress(Snappy, "a"u8.ToArray()))).IsEqualTo("010061");
        await Assert.That(TestBytes.ToHex(CodecTestData.Compress(Snappy, Enumerable.Repeat((byte)'a', 100).ToArray())))
            .IsEqualTo("640061FE01008A0100");
    }

    [Test]
    public async Task MalformedStreams_AreRejected()
    {
        (string Reason, string Hex, int Length)[] cases =
        [
            ("preamble missing", "", 5),
            ("preamble cut off", "85", 5),
            ("preamble too long", "80 80 80 80 80 01", 5),
            ("declared length differs from the header", "05 10 68656C6C6F", 4),
            ("literal runs past the data", "05 10 68656C", 5),
            ("literal runs past the declared size", "03 10 68656C6C6F", 3),
            ("literal length bytes cut off", "05 F4 04", 5),
            ("copy tag cut off", "04 0C 61626364 01", 8),
            ("copy2 offset cut off", "08 0C 61626364 16 08", 8),
            ("copy4 offset cut off", "08 0C 61626364 17 080000", 8),
            ("copy offset zero", "08 0C 61626364 01 00", 8),
            ("copy reaches before the start", "08 0C 61626364 01 05", 8),
            ("copy with an enormous offset", "08 0C 61626364 17 FFFFFFFF", 8),
            ("copy runs past the declared size", "06 0C 61626364 01 04", 6),
            ("output shorter than declared", "08 0C 61626364", 8),
            ("literal length beyond int range", "05 FC FFFFFFFF", 5),
        ];

        foreach (var (reason, hex, length) in cases)
        {
            var data = TestBytes.Hex(hex);

            await Assert.That(() => Snappy.Decompress(data, new byte[length])).Throws<ParquetFormatException>().Because(reason);
        }
    }

    [Test]
    public async Task Decoder_NeverWritesOutsideTheDestination()
    {
        // A destination surrounded by guard bytes: nothing outside the slice may change, even for bad input.
        var random = new Random(31);
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            var input = new byte[random.Next(1, 64)];
            random.NextBytes(input);
            int length = random.Next(0, 40);
            var buffer = new byte[length + 16];
            Array.Fill(buffer, (byte)0xAB);

            try
            {
                Snappy.Decompress(input, buffer.AsSpan(8, length));
            }
            catch (ParquetFormatException)
            {
            }

            await Assert.That(buffer.AsSpan(0, 8).ToArray().All(b => b == 0xAB)).IsTrue();
            await Assert.That(buffer.AsSpan(8 + length).ToArray().All(b => b == 0xAB)).IsTrue();
        }
    }

    [Test]
    public async Task MaxCompressedLength_MatchesTheReferenceFormula()
    {
        await Assert.That(Snappy.GetMaxCompressedLength(0)).IsEqualTo(32);
        await Assert.That(Snappy.GetMaxCompressedLength(6000)).IsEqualTo(32 + 6000 + 1000);
        await Assert.That(() => Snappy.GetMaxCompressedLength(int.MaxValue)).Throws<ArgumentOutOfRangeException>();
    }

    private static string Decode(string hex, int length) => Decode(TestBytes.Hex(hex), length);

    private static string Decode(byte[] data, int length) =>
        System.Text.Encoding.ASCII.GetString(CodecTestData.Decompress(Snappy, data, length));
}
