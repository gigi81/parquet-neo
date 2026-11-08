using ParquetNeo.ColumnTypes;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Logical;

public class DecimalConversionTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Test]
    [Arguments("00", 0, "0")]
    [Arguments("01", 0, "1")]
    [Arguments("7F", 0, "127")]
    [Arguments("80", 0, "-128")]
    [Arguments("FF", 0, "-1")]
    [Arguments("FFFF", 2, "-0.01")]
    [Arguments("0080", 0, "128")]
    [Arguments("FF7F", 0, "-129")]
    [Arguments("000000000000000000000001", 3, "0.001")] // longer than needed, still sign extended
    [Arguments("FFFFFFFFFFFFFFFFFFFFFFFF", 0, "-1")]
    [Arguments("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", 0, "-1")] // 18 bytes: more than an Int128 holds, all sign extension
    [Arguments("0000000000000000000000000000000000FF", 0, "255")]
    public async Task BigEndianBytes_AreReadAsTwosComplement(string hex, int scale, string expected)
    {
        await Assert.That(DecimalConversion.FromBigEndian(Hex(hex), scale)).IsEqualTo(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task TheExtremes_OfADecimal_AreRead()
    {
        // 96 bits of magnitude: 0xFFFFFFFFFFFFFFFFFFFFFFFF, needing 13 bytes with the sign.
        await Assert.That(DecimalConversion.FromBigEndian(Hex("00FFFFFFFFFFFFFFFFFFFFFFFF"), 0)).IsEqualTo(decimal.MaxValue);
        await Assert.That(DecimalConversion.FromBigEndian(Hex("FF000000000000000000000001"), 0)).IsEqualTo(decimal.MinValue);
        await Assert.That(DecimalConversion.FromBigEndian(Hex("00FFFFFFFFFFFFFFFFFFFFFFFF"), 28)).IsEqualTo(7.9228162514264337593543950335m);
    }

    [Test]
    [Arguments("")]
    [Arguments("0100000000000000000000000000")] // 2^104, 14 bytes
    [Arguments("0001000000000000000000000000")] // 2^96, one bit too many
    [Arguments("FFFEFFFFFFFFFFFFFFFFFFFFFFFF")] // -(2^96 + 1), ditto
    [Arguments("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // 18 bytes whose first two are not sign extension
    [Arguments("FF7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // the same for a negative value
    [Arguments("FFFF7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // 18 bytes whose sign flips at the 16-byte boundary
    [Arguments("0080000000000000000000000000000000FF")]
    [Arguments("010000000000000000000000000000000000")] // 18 bytes: 2^136
    [Arguments("0100000000000000000000000000000000")] // 17 bytes: 2^128, which an Int128 would silently shift out
    [Arguments("FF7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // 17 bytes, negative, whose 16 byte tail is positive
    public async Task BytesThatAreEmptyOrTooLarge_AreRejected(string hex)
    {
        await Assert.That(() => DecimalConversion.FromBigEndian(Hex(hex), 0)).Throws<ValueConversionException>();
    }

    [Test]
    public async Task Integers_OfAnyWidth_KeepTheirSign_AndScale()
    {
        await Assert.That(DecimalConversion.FromInt64(long.MinValue, 0)).IsEqualTo(-9223372036854775808m);
        await Assert.That(DecimalConversion.FromInt64(long.MaxValue, 0)).IsEqualTo(9223372036854775807m);
        await Assert.That(DecimalConversion.FromInt64(int.MinValue, 2)).IsEqualTo(-21474836.48m);
        await Assert.That(DecimalConversion.FromInt64(-5, 28)).IsEqualTo(-0.0000000000000000000000000005m);
        await Assert.That(DecimalConversion.FromInt128(-(Int128.One << 95), 0)).IsEqualTo(-39614081257132168796771975168m);
        await Assert.That(() => DecimalConversion.FromInt128(Int128.One << 96, 0)).Throws<ValueConversionException>();
        await Assert.That(() => DecimalConversion.FromInt128(Int128.MinValue, 0)).Throws<ValueConversionException>();
    }

    [Test]
    [Arguments("1.5", 2, 9, "150")]
    [Arguments("1.50", 2, 9, "150")]
    [Arguments("1.500", 2, 9, "150")] // trailing zeros beyond the scale are dropped without loss
    [Arguments("-1.5", 2, 9, "-150")]
    [Arguments("0", 2, 9, "0")]
    [Arguments("12", 0, 9, "12")]
    [Arguments("-0.01", 2, 9, "-1")]
    [Arguments("9999999.99", 2, 9, "999999999")]
    [Arguments("-9999999.99", 2, 9, "-999999999")]
    [Arguments("79228162514264337593543950335", 0, 29, "79228162514264337593543950335")]
    [Arguments("7.9228162514264337593543950335", 28, 29, "79228162514264337593543950335")]
    [Arguments("0.0000000000000000000000000001", 28, 28, "1")]
    public async Task ADecimal_BecomesTheUnscaledIntegerOfTheColumn(string value, int scale, int precision, string expected)
    {
        var unscaled = DecimalConversion.ToUnscaled(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), scale, precision);

        await Assert.That(unscaled.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.555", 2, 9)] // would lose the last digit
    [Arguments("0.001", 2, 9)]
    [Arguments("-0.0000000000000000000000000001", 27, 28)]
    [Arguments("10000000.00", 2, 9)] // ten digits at a precision of nine
    [Arguments("-10000000.00", 2, 9)]
    [Arguments("1000000000", 0, 9)]
    [Arguments("79228162514264337593543950335", 28, 38)] // 57 digits unscaled: more than an Int128 holds
    [Arguments("79228162514264337593543950335", 0, 28)] // 29 digits at a precision of 28
    public async Task ADecimalTheColumnCannotStore_IsRejected(string value, int scale, int precision)
    {
        await Assert.That(() => DecimalConversion.ToUnscaled(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), scale, precision))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task APrecisionAbove38_IsLimitedByThe127BitsOfAnInt128()
    {
        // 1e9 at a scale of 28 is the integer 1e37; 2e10 would be 2e38, which is below 10^40 but above the 2^127 an Int128 holds.
        var big = DecimalConversion.ToUnscaled(1000000000m, 28, 40);

        await Assert.That(big).IsEqualTo((Int128)System.Numerics.BigInteger.Pow(10, 37));
        await Assert.That(() => DecimalConversion.ToUnscaled(20000000000m, 28, 40)).Throws<ArgumentOutOfRangeException>();
        // At a precision of 38 the limit is 10^38, below the 2^127 an Int128 holds: 1.5e38 fits an Int128 but not the precision.
        await Assert.That(() => DecimalConversion.ToUnscaled(15000000000m, 28, 38)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(DecimalConversion.ToUnscaled(9999999999m, 28, 38).ToString()).IsEqualTo("99999999990000000000000000000000000000");
    }

    [Test]
    public async Task WholeInt128Bytes_AreWrittenAndChecked()
    {
        var top = new byte[16];
        DecimalConversion.WriteBigEndian(Int128.One << 125, top);
        var negativeTop = new byte[16];
        DecimalConversion.WriteBigEndian(-(Int128.One << 125), negativeTop);
        var fifteen = new byte[15];
        DecimalConversion.WriteBigEndian((Int128.One << 119) - 1, fifteen);

        await Assert.That(Convert.ToHexString(top)).IsEqualTo("20" + new string('0', 30));
        await Assert.That(Convert.ToHexString(negativeTop)).IsEqualTo("E0" + new string('0', 30));
        await Assert.That(Convert.ToHexString(fifteen)).IsEqualTo("7F" + new string('F', 28));
        await Assert.That(() => DecimalConversion.WriteBigEndian(Int128.One << 119, new byte[15])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => DecimalConversion.WriteBigEndian(-(Int128.One << 119) - 1, new byte[15])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(127, 1)]
    [Arguments(128, 2)]
    [Arguments(-128, 1)]
    [Arguments(-129, 2)]
    [Arguments(32767, 2)]
    [Arguments(32768, 3)]
    [Arguments(-32768, 2)]
    [Arguments(int.MaxValue, 4)]
    [Arguments(int.MinValue, 4)]
    public async Task TheShortestForm_OfAnInteger_IsFound(int value, int length)
    {
        await Assert.That(DecimalConversion.MinimalLength(value)).IsEqualTo(length);
    }

    [Test]
    public async Task TheShortestForm_OfLargeIntegers_IsFound()
    {
        await Assert.That(DecimalConversion.MinimalLength((Int128.One << 119) - 1)).IsEqualTo(15);
        await Assert.That(DecimalConversion.MinimalLength(Int128.One << 119)).IsEqualTo(16);
        await Assert.That(DecimalConversion.MinimalLength(-(Int128.One << 119))).IsEqualTo(15);
        await Assert.That(DecimalConversion.MinimalLength(-(Int128.One << 119) - 1)).IsEqualTo(16);
        await Assert.That(DecimalConversion.MinimalLength(Int128.MaxValue)).IsEqualTo(16);
        await Assert.That(DecimalConversion.MinimalLength(Int128.MinValue)).IsEqualTo(16);
    }

    [Test]
    public async Task IntegersAreWrittenSignExtended_IntoAnyWidth()
    {
        var wide = new byte[20];
        var narrow = new byte[2];
        var one = new byte[1];

        DecimalConversion.WriteBigEndian(-2, wide);
        DecimalConversion.WriteBigEndian(0x1234, narrow);
        DecimalConversion.WriteBigEndian(-128, one);

        await Assert.That(Convert.ToHexString(wide)).IsEqualTo("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFE");
        await Assert.That(Convert.ToHexString(narrow)).IsEqualTo("1234");
        await Assert.That(Convert.ToHexString(one)).IsEqualTo("80");
        DecimalConversion.WriteBigEndian(Int128.One << 100, wide);
        await Assert.That(Convert.ToHexString(wide)).IsEqualTo(new string('0', 14) + "1" + new string('0', 25));
    }

    [Test]
    [Arguments(128, 1)]
    [Arguments(-129, 1)]
    [Arguments(32768, 2)]
    [Arguments(-32769, 2)]
    [Arguments(8388608, 3)]
    public async Task AnIntegerThatDoesNotFit_TheWidth_IsRejected(int value, int width)
    {
        await Assert.That(() => DecimalConversion.WriteBigEndian(value, new byte[width])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RandomDecimals_RoundTrip_ThroughBytes_AtEveryScale()
    {
        var random = new Random(7);
        for (int scale = 0; scale <= 28; scale++)
        {
            for (int i = 0; i < 200; i++)
            {
                var value = new decimal(random.Next(), random.Next(), random.Next(0, 3) == 0 ? 0 : random.Next(), random.Next(2) == 0, (byte)scale);
                var bytes = new byte[16];
                var unscaled = DecimalConversion.ToUnscaled(value, scale, 38);
                int length = DecimalConversion.MinimalLength(unscaled);
                DecimalConversion.WriteBigEndian(unscaled, bytes.AsSpan(0, length));

                var back = DecimalConversion.FromBigEndian(bytes.AsSpan(0, length), scale);

                await Assert.That(back).IsEqualTo(value).Because($"scale {scale}: {value}");
                await Assert.That((decimal.GetBits(back)[3] >> 16) & 0xFF).IsEqualTo(scale);
            }
        }
    }
}
