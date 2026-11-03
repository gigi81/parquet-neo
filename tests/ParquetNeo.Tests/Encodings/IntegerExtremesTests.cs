using System.Buffers;
using System.Numerics;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Encodings;

/// <summary>
/// Minimum and maximum values of every integer type, through the bit-packing, RLE and PLAIN encodings,
/// in real pyarrow pages and in column statistics. The spec defines INT32 and INT64 as signed, but an
/// INT(n, false) annotation makes the very same bytes mean an unsigned number, so each check says which
/// reading it applies.
/// </summary>
public class IntegerExtremesTests
{
    private static readonly string[] Columns = ["int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64"];

    // ---- RLE / bit-packing: values are unsigned bit patterns of up to 32 bits ----

    [Test]
    public async Task RleRuns_AtWidth32_CoverTheSignBoundary()
    {
        // Three runs of length 1: 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, each as 4 little-endian bytes.
        var data = TestBytes.Hex("02 FFFFFF7F  02 00000080  02 FFFFFFFF");

        var values = Decode(data, 32, 3);

        await Assert.That(values).IsEquivalentTo(new[] { int.MaxValue, int.MinValue, -1 });
        // Read as unsigned, which is how a bit width of 32 is defined, nothing is lost.
        await Assert.That(values.Select(v => (uint)v)).IsEquivalentTo(new[] { 0x7FFFFFFFu, 0x80000000u, 0xFFFFFFFFu });
    }

    [Test]
    public async Task BitPackedGroup_AtWidth32_IsEightLittleEndianWords()
    {
        var data = TestBytes.Hex("03  00000000 01000000 FFFFFF7F 00000080 01000080 FEFFFFFF FFFFFFFF 78563412");

        var values = Decode(data, 32, 8);

        await Assert.That(values.Select(v => (uint)v)).IsEquivalentTo(
            new[] { 0u, 1u, 0x7FFFFFFFu, 0x80000000u, 0x80000001u, 0xFFFFFFFEu, 0xFFFFFFFFu, 0x12345678u });
    }

    [Test]
    public async Task MaximumValueOfEveryWidth_DecodesExactly_AsRepeatedRunsAndAsBitPackedGroups()
    {
        for (int width = 1; width <= 32; width++)
        {
            uint max = (uint)((1UL << width) - 1);
            int asInt = unchecked((int)max);

            // 32 equal values become an RLE run; alternating with zero stays bit-packed.
            var repeated = Enumerable.Repeat(asInt, 32).ToArray();
            var alternating = Enumerable.Range(0, 32).Select(i => i % 2 == 0 ? asInt : 0).ToArray();

            await Assert.That(Decode(Encode(repeated, width), width, 32).Select(v => (uint)v).All(v => v == max)).IsTrue();
            await Assert.That(Decode(Encode(alternating, width), width, 32).Select(v => (uint)v)
                .SequenceEqual(alternating.Select(v => (uint)v))).IsTrue();
        }
    }

    [Test]
    public async Task Width31_ReachesInt32MaxValueAsAPositiveNumber()
    {
        var values = Decode(Encode([int.MaxValue, 0, int.MaxValue, 1, 2, 3, 4, 5, 6], 31), 31, 9);

        await Assert.That(values[0]).IsEqualTo(int.MaxValue);
        await Assert.That(values[2]).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Width32_IsTheOnlyWidthWhereNegativeInts_AreValidInput()
    {
        // At width 32 an int is just a carrier for the 32-bit pattern; below that a set top bit cannot fit.
        await Assert.That(Decode(Encode([int.MinValue, -1], 32), 32, 2)).IsEquivalentTo(new[] { int.MinValue, -1 });
        await Assert.That(() => Encode([int.MinValue], 31)).Throws<ArgumentException>();
        await Assert.That(() => Encode([-1], 31)).Throws<ArgumentException>();
        await Assert.That(() => Encode([int.MaxValue], 30)).Throws<ArgumentException>();
    }

    // ---- PLAIN: two's complement, little endian ----

    [Test]
    public async Task PlainInt32_MinAndMax_AreTwosComplementLittleEndian()
    {
        int[] values = [int.MinValue, int.MaxValue, -1, 0, 1];

        var encoded = WritePlain(w => PlainEncoder.WriteInt32s(values, w));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo("00000080" + "FFFFFF7F" + "FFFFFFFF" + "00000000" + "01000000");
        var decoded = new int[5];
        PlainDecoder.ReadInt32s(encoded, decoded);
        await Assert.That(decoded).IsEquivalentTo(values);
    }

    [Test]
    public async Task PlainInt64_MinAndMax_AreTwosComplementLittleEndian()
    {
        long[] values = [long.MinValue, long.MaxValue, -1, 0, 1];

        var encoded = WritePlain(w => PlainEncoder.WriteInt64s(values, w));

        await Assert.That(TestBytes.ToHex(encoded)).IsEqualTo(
            "0000000000000080" + "FFFFFFFFFFFFFF7F" + "FFFFFFFFFFFFFFFF" + "0000000000000000" + "0100000000000000");
        var decoded = new long[5];
        PlainDecoder.ReadInt64s(encoded, decoded);
        await Assert.That(decoded).IsEquivalentTo(values);
    }

    [Test]
    public async Task UnsignedValues_StoredInSignedPhysicalTypes_KeepTheirBitPattern()
    {
        uint[] unsigned32 = [0, 1, int.MaxValue, 0x80000000, uint.MaxValue];
        ulong[] unsigned64 = [0, 1, long.MaxValue, 0x8000000000000000, ulong.MaxValue];

        var encoded32 = WritePlain(w => PlainEncoder.WriteInt32s(unsigned32.Select(v => unchecked((int)v)).ToArray(), w));
        var encoded64 = WritePlain(w => PlainEncoder.WriteInt64s(unsigned64.Select(v => unchecked((long)v)).ToArray(), w));
        var decoded32 = new int[5];
        var decoded64 = new long[5];
        PlainDecoder.ReadInt32s(encoded32, decoded32);
        PlainDecoder.ReadInt64s(encoded64, decoded64);

        await Assert.That(TestBytes.ToHex(encoded32)).IsEqualTo("00000000" + "01000000" + "FFFFFF7F" + "00000080" + "FFFFFFFF");
        await Assert.That(decoded32.Select(v => unchecked((uint)v))).IsEquivalentTo(unsigned32);
        await Assert.That(decoded64.Select(v => unchecked((ulong)v))).IsEquivalentTo(unsigned64);
    }

    // ---- real pyarrow pages ----

    [Test]
    [Arguments("pyarrow-int-extremes-plain-v1.parquet")]
    [Arguments("pyarrow-int-extremes-plain-v2.parquet")]
    public async Task PlainPages_DecodeTheExtremesOfEveryIntegerType(string fixture)
    {
        var file = LoadFixture(fixture);

        foreach (string column in Columns)
        {
            var chunk = file.Chunk(column);
            var page = file.ReadPage(chunk.DataPageOffset);
            var values = DecodePlain(file, column, page.Values, 5);

            await Assert.That(chunk.DictionaryPageOffset).IsNull();
            await Assert.That(values).IsEquivalentTo(ExpectedValues(file, column));
        }
    }

    [Test]
    [Arguments("pyarrow-int-extremes-dict-v1.parquet")]
    [Arguments("pyarrow-int-extremes-dict-v2.parquet")]
    public async Task DictionaryPages_DecodeTheExtremesOfEveryIntegerType(string fixture)
    {
        var file = LoadFixture(fixture);

        foreach (string column in Columns)
        {
            var chunk = file.Chunk(column);
            var dictionaryPage = file.ReadPage(chunk.DictionaryPageOffset!.Value);
            var dictionary = DecodePlain(file, column, dictionaryPage.Values, dictionaryPage.NumValues);

            // Data page: one byte with the bit width, then RLE-encoded dictionary indices.
            var dataPage = file.ReadPage(chunk.DataPageOffset);
            int bitWidth = dataPage.Values[0];
            var indices = Decode(dataPage.Values[1..], bitWidth, 5);
            var values = indices.Select(i => dictionary[i]).ToArray();

            await Assert.That(dataPage.Encoding is ParquetEncoding.PlainDictionary or ParquetEncoding.RleDictionary).IsTrue();
            await Assert.That(dictionaryPage.NumValues).IsEqualTo(5);
            await Assert.That(values).IsEquivalentTo(ExpectedValues(file, column));
        }
    }

    [Test]
    [Arguments("pyarrow-int-extremes-plain-v1.parquet")]
    [Arguments("pyarrow-int-extremes-plain-v2.parquet")]
    [Arguments("pyarrow-int-extremes-dict-v1.parquet")]
    [Arguments("pyarrow-int-extremes-dict-v2.parquet")]
    public async Task Statistics_MinAndMax_DecodeToTheExtremes(string fixture)
    {
        var file = LoadFixture(fixture);

        foreach (string column in Columns)
        {
            var (bits, signed) = file.IntegerType(column);
            var stats = file.Chunk(column).Statistics!;

            // min_value / max_value are PLAIN encoded (little-endian physical value), ordered by the logical type.
            var min = DecodePlain(file, column, stats.MinValue!, 1)[0];
            var max = DecodePlain(file, column, stats.MaxValue!, 1)[0];

            BigInteger expectedMin = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
            BigInteger expectedMax = signed ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
            await Assert.That(min).IsEqualTo(expectedMin);
            await Assert.That(max).IsEqualTo(expectedMax);
        }
    }

    [Test]
    public async Task Statistics_RawBytes_ArePlainEncodedPhysicalValues()
    {
        var file = LoadFixture("pyarrow-int-extremes-plain-v1.parquet");

        (string Column, string MinHex, string MaxHex)[] expected =
        [
            // Signed small types are sign-extended into the INT32 carrier ...
            ("int8", "80FFFFFF", "7F000000"),
            ("int16", "0080FFFF", "FF7F0000"),
            ("int32", "00000080", "FFFFFF7F"),
            ("int64", "0000000000000080", "FFFFFFFFFFFFFF7F"),
            // ... unsigned ones are zero-extended, and the full-width unsigned maxima use every bit.
            ("uint8", "00000000", "FF000000"),
            ("uint16", "00000000", "FFFF0000"),
            ("uint32", "00000000", "FFFFFFFF"),
            ("uint64", "0000000000000000", "FFFFFFFFFFFFFFFF"),
        ];

        foreach (var (column, minHex, maxHex) in expected)
        {
            var stats = file.Chunk(column).Statistics!;
            await Assert.That(TestBytes.ToHex(stats.MinValue!)).IsEqualTo(minHex);
            await Assert.That(TestBytes.ToHex(stats.MaxValue!)).IsEqualTo(maxHex);
        }
    }

    [Test]
    public async Task Statistics_UnsignedColumns_AreOrderedUnsigned_NotBySignedComparison()
    {
        var file = LoadFixture("pyarrow-int-extremes-plain-v1.parquet");

        // Read as signed 32-bit numbers the bytes of a uint32 column's max would be -1, below its min of 0.
        // The ordering of the column (here: unsigned, from its INT(32, false) annotation) is what applies.
        var stats = file.Chunk("uint32").Statistics!;
        var signedMin = new int[1];
        var signedMax = new int[1];
        PlainDecoder.ReadInt32s(stats.MinValue!, signedMin);
        PlainDecoder.ReadInt32s(stats.MaxValue!, signedMax);

        await Assert.That(signedMax[0]).IsLessThan(signedMin[0]); // the trap for a reader that ignores the logical type
        await Assert.That(file.IntegerType("uint32")).IsEqualTo((32, false));
        await Assert.That(unchecked((uint)signedMax[0])).IsGreaterThan(unchecked((uint)signedMin[0]));
    }

    [Test]
    public async Task PlainInt32AndInt64Columns_CarryNoAnnotation_AndAreImplicitlySigned()
    {
        var file = LoadFixture("pyarrow-int-extremes-plain-v1.parquet");

        // The spec treats INT(32, true) / INT(64, true) as implied, so writers may omit them (pyarrow does).
        await Assert.That(file.Schema.FindColumn(["int32"])!.LogicalType).IsNull();
        await Assert.That(file.Schema.FindColumn(["int64"])!.LogicalType).IsNull();
        await Assert.That(file.IntegerType("int32")).IsEqualTo((32, true));
        await Assert.That(file.IntegerType("int64")).IsEqualTo((64, true));

        // Every other width carries an explicit annotation.
        await Assert.That(file.Schema.FindColumn(["uint32"])!.LogicalType).IsEqualTo(LogicalType.Integer(32, isSigned: false));
        await Assert.That(file.Schema.FindColumn(["int8"])!.LogicalType).IsEqualTo(LogicalType.Integer(8, isSigned: true));
    }

    [Test]
    public async Task ColumnOrders_AreTypeDefined_SoStatisticsFollowTheLogicalType()
    {
        var file = LoadFixture("pyarrow-int-extremes-plain-v1.parquet");

        await Assert.That(file.Metadata.ColumnOrders!.Count).IsEqualTo(Columns.Length);
        await Assert.That(file.Metadata.ColumnOrders.All(o => o == ColumnOrder.TypeDefined)).IsTrue();
    }

    // ---- helpers ----

    /// <summary>Decodes the values of an integer column to exact numbers, honouring signedness and width.</summary>
    private static BigInteger[] DecodePlain(FixtureFile file, string column, byte[] bytes, int count)
    {
        var (_, signed) = file.IntegerType(column);
        var physical = file.Schema.FindColumn([column])!.Type;
        var result = new BigInteger[count];
        if (physical == PhysicalType.Int32)
        {
            var values = new int[count];
            PlainDecoder.ReadInt32s(bytes, values);
            for (int i = 0; i < count; i++)
            {
                result[i] = signed ? values[i] : unchecked((uint)values[i]);
            }
        }
        else
        {
            var values = new long[count];
            PlainDecoder.ReadInt64s(bytes, values);
            for (int i = 0; i < count; i++)
            {
                result[i] = signed ? values[i] : unchecked((ulong)values[i]);
            }
        }

        return result;
    }

    /// <summary>What the generator wrote: signed -2^(b-1), -1, 0, 1, 2^(b-1)-1; unsigned 0, 1, 2^(b-1)-1, 2^(b-1), 2^b-1.</summary>
    private static BigInteger[] ExpectedValues(FixtureFile file, string column)
    {
        var (bits, signed) = file.IntegerType(column);
        var half = BigInteger.One << (bits - 1);
        return signed
            ? [-half, -1, 0, 1, half - 1]
            : [0, 1, half - 1, half, (half << 1) - 1];
    }

    private static FixtureFile LoadFixture(string name) => new(File.ReadAllBytes(TestBytes.FixturePath(name)));

    private static byte[] Encode(int[] values, int width)
    {
        var buffer = new ArrayBufferWriter<byte>();
        RleBitPackedEncoder.Encode(values, width, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static int[] Decode(byte[] data, int width, int count)
    {
        var values = new int[count];
        int read = new RleBitPackedDecoder(data, width).ReadValues(values);
        if (read != count)
        {
            throw new InvalidOperationException($"Expected {count} values but the data held {read}.");
        }

        return values;
    }

    private static byte[] WritePlain(Action<IBufferWriter<byte>> action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        action(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed record PageData(ParquetEncoding Encoding, int NumValues, byte[] Values);

    private sealed class FixtureFile
    {
        private readonly byte[] _bytes;

        public FixtureFile(byte[] bytes)
        {
            _bytes = bytes;
            Metadata = FileMetaData.Deserialize(TestBytes.ReadFooterBytes(bytes));
            Schema = ParquetSchema.FromElements(Metadata.Schema);
        }

        public FileMetaData Metadata { get; }

        public ParquetSchema Schema { get; }

        public ColumnMetaData Chunk(string column) =>
            Metadata.RowGroups[0].Columns.Single(c => c.MetaData!.PathInSchema[0] == column).MetaData!;

        /// <summary>
        /// Bit width and signedness from the column's INT(n, signed) annotation. Without an annotation the
        /// spec says INT(32, true) and INT(64, true) are implied by the INT32 and INT64 physical types.
        /// </summary>
        public (int Bits, bool Signed) IntegerType(string column)
        {
            var node = Schema.FindColumn([column])!;
            if (node.LogicalType is { Kind: LogicalTypeKind.Integer } logical)
            {
                return (logical.BitWidth, logical.IsSigned);
            }

            return node.Type == PhysicalType.Int32 ? (32, true) : (64, true);
        }

        /// <summary>The page at <paramref name="offset"/> with its levels (none in these required columns) removed.</summary>
        public PageData ReadPage(long offset)
        {
            var header = PageHeader.Deserialize(_bytes.AsSpan((int)offset), out int headerLength);
            var page = _bytes.AsSpan((int)offset + headerLength, header.CompressedPageSize).ToArray();

            if (header.DictionaryPageHeader is { } dictionary)
            {
                return new PageData(dictionary.Encoding, dictionary.NumValues, page);
            }

            if (header.DataPageHeaderV2 is { } v2)
            {
                int levels = v2.RepetitionLevelsByteLength + v2.DefinitionLevelsByteLength;
                return new PageData(v2.Encoding, v2.NumValues, page[levels..]);
            }

            return new PageData(header.DataPageHeader!.Encoding, header.DataPageHeader.NumValues, page);
        }
    }
}
