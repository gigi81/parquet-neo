using ParquetNeo.Thrift;

namespace ParquetNeo.Tests.Thrift;

public class ThriftCompactProtocolTests
{
    [Test]
    [Arguments(0, "00")]
    [Arguments(1, "02")]
    [Arguments(-1, "01")]
    [Arguments(2, "04")]
    [Arguments(63, "7E")]
    [Arguments(64, "8001")]
    [Arguments(300, "D804")]
    [Arguments(int.MaxValue, "FEFFFFFF0F")]
    [Arguments(int.MinValue, "FFFFFFFF0F")]
    public async Task I32_IsZigZagVarint(int value, string hex)
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) => w.WriteI32(value));
        int read = TestBytes.Read(written, (ref ThriftCompactReader r) => r.ReadI32());

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo(hex);
        await Assert.That(read).IsEqualTo(value);
    }

    [Test]
    [Arguments(0L, "00")]
    [Arguments(-1L, "01")]
    [Arguments(1L, "02")]
    [Arguments(long.MaxValue, "FEFFFFFFFFFFFFFFFF01")]
    [Arguments(long.MinValue, "FFFFFFFFFFFFFFFFFF01")]
    public async Task I64_IsZigZagVarint(long value, string hex)
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) => w.WriteI64(value));
        long read = TestBytes.Read(written, (ref ThriftCompactReader r) => r.ReadI64());

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo(hex);
        await Assert.That(read).IsEqualTo(value);
    }

    [Test]
    public async Task Double_IsLittleEndian()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) => w.WriteDouble(1.5));
        double read = TestBytes.Read(written, (ref ThriftCompactReader r) => r.ReadDouble());

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("000000000000F83F");
        await Assert.That(read).IsEqualTo(1.5);
    }

    [Test]
    [Arguments("", "00")]
    [Arguments("abc", "03616263")]
    [Arguments("é", "02C3A9")]
    public async Task String_IsLengthPrefixedUtf8(string value, string hex)
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) => w.WriteString(value));
        string read = TestBytes.Read(written, (ref ThriftCompactReader r) => r.ReadString());

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo(hex);
        await Assert.That(read).IsEqualTo(value);
    }

    [Test]
    public async Task Struct_WithSingleI32Field_MatchesThriftReferenceBytes()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(1, 1);
            w.EndStruct();
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("150200");
    }

    [Test]
    public async Task FieldIdsFarApartUseLongFormHeader()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(20, 1); // delta 20 > 15
            w.EndStruct();
        });
        var (id, type) = TestBytes.Read(written, (ref ThriftCompactReader r) =>
        {
            r.BeginStruct();
            r.TryReadField(out short fieldId, out var fieldType);
            return (fieldId, fieldType);
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("05280200");
        await Assert.That(id).IsEqualTo((short)20);
        await Assert.That(type).IsEqualTo(CompactType.I32);
    }

    [Test]
    public async Task BoolFieldsCarryTheirValueInTheHeader()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteBoolField(1, true);
            w.WriteBoolField(2, false);
            w.EndStruct();
        });
        var types = TestBytes.Read(written, (ref ThriftCompactReader r) =>
        {
            r.BeginStruct();
            r.TryReadField(out _, out var first);
            r.TryReadField(out _, out var second);
            return (first, second);
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("111200");
        await Assert.That(types.first).IsEqualTo(CompactType.BoolTrue);
        await Assert.That(types.second).IsEqualTo(CompactType.BoolFalse);
    }

    [Test]
    public async Task NestedStructRestartsFieldIdsAndRestoresThemAfterwards()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.BeginStructField(1);
            w.WriteI32Field(1, 1);
            w.EndStruct();
            w.WriteI32Field(2, 2);
            w.EndStruct();
        });
        var ids = TestBytes.Read(written, (ref ThriftCompactReader r) =>
        {
            var found = new List<short>();
            r.BeginStruct();
            while (r.TryReadField(out short id, out var type))
            {
                found.Add(id);
                if (type == CompactType.Struct)
                {
                    r.BeginStruct();
                    while (r.TryReadField(out short inner, out var innerType))
                    {
                        found.Add((short)(inner * 10));
                        r.Skip(innerType);
                    }

                    r.EndStruct();
                }
                else
                {
                    r.Skip(type);
                }
            }

            r.EndStruct();
            return found;
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("1C150200150400");
        await Assert.That(ids).IsEquivalentTo(new short[] { 1, 10, 2 });
    }

    [Test]
    public async Task ShortListHeaderPacksCountAndElementType()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.WriteListHeader(CompactType.I32, 3);
            w.WriteI32(1);
            w.WriteI32(2);
            w.WriteI32(3);
        });
        var values = TestBytes.Read(written, (ref ThriftCompactReader r) =>
        {
            var items = new int[r.ReadListHeader(CompactType.I32)];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = r.ReadI32();
            }

            return items;
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("35020406");
        await Assert.That(values).IsEquivalentTo(new[] { 1, 2, 3 });
    }

    [Test]
    public async Task ListWithFifteenOrMoreElementsUsesLongHeader()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.WriteListHeader(CompactType.I32, 15);
            for (int i = 0; i < 15; i++)
            {
                w.WriteI32(0);
            }
        });
        int count = TestBytes.Read(written, (ref ThriftCompactReader r) => r.ReadListHeader(CompactType.I32));

        await Assert.That(TestBytes.ToHex(written[..2])).IsEqualTo("F50F");
        await Assert.That(count).IsEqualTo(15);
    }

    [Test]
    public async Task BoolListElementsOccupyOneByteEach()
    {
        var written = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.WriteListHeader(CompactType.BoolTrue, 2);
            w.WriteBoolElement(true);
            w.WriteBoolElement(false);
        });
        var values = TestBytes.Read(written, (ref ThriftCompactReader r) =>
        {
            r.ReadListHeader(CompactType.BoolTrue);
            return (r.ReadBoolElement(), r.ReadBoolElement());
        });

        await Assert.That(TestBytes.ToHex(written)).IsEqualTo("210102");
        await Assert.That(values.Item1).IsTrue();
        await Assert.That(values.Item2).IsFalse();
    }

    [Test]
    public async Task ReadListHeader_RejectsUnexpectedElementType()
    {
        var data = TestBytes.Hex("15 02");

        await Assert.That(() => TestBytes.Read(data, (ref ThriftCompactReader r) => r.ReadListHeader(CompactType.I64)))
            .Throws<ParquetFormatException>();
    }

    // One field of every wire type (map and set included), then a sentinel i32 field with value 42.
    private const string AllTypesStruct =
        "11 " +                          // 1: bool true
        "13 05 " +                       // 2: i8 5
        "14 D8 04 " +                    // 3: i16 300
        "15 01 " +                       // 4: i32 -1
        "16 02 " +                       // 5: i64 1
        "17 000000000000F83F " +         // 6: double 1.5
        "18 02 6162 " +                  // 7: binary "ab"
        "19 25 02 04 " +                 // 8: list<i32> [1, 2]
        "19 21 01 02 " +                 // 9: list<bool> [true, false]
        "1B 01 85 01 61 02 " +           // 10: map<binary, i32> {"a": 1}
        "1C 15 02 00 " +                 // 11: struct { 1: i32 1 }
        "1A 15 0E " +                    // 12: set<i32> [7]
        "15 54 " +                       // 13: i32 42
        "00";

    [Test]
    public async Task Skip_PassesOverEveryType()
    {
        var data = TestBytes.Hex(AllTypesStruct);

        var (skipped, sentinel, end) = TestBytes.Read(data, (ref ThriftCompactReader r) =>
        {
            int skippedCount = 0;
            int value = 0;
            r.BeginStruct();
            while (r.TryReadField(out short id, out var type))
            {
                if (id == 13)
                {
                    value = r.ReadI32();
                }
                else
                {
                    r.Skip(type);
                    skippedCount++;
                }
            }

            r.EndStruct();
            return (skippedCount, value, r.Remaining);
        });

        await Assert.That(skipped).IsEqualTo(12);
        await Assert.That(sentinel).IsEqualTo(42);
        await Assert.That(end).IsEqualTo(0);
    }

    [Test]
    public async Task Skip_AcceptsEmptyMap()
    {
        var data = TestBytes.Hex("1B 00 00");

        int remaining = TestBytes.Read(data, (ref ThriftCompactReader r) =>
        {
            r.BeginStruct();
            r.TryReadField(out _, out var type);
            r.Skip(type);
            r.TryReadField(out _, out _); // stop
            r.EndStruct();
            return r.Remaining;
        });

        await Assert.That(remaining).IsEqualTo(0);
    }

    [Test]
    [Arguments("15", "truncated varint field")]
    [Arguments("15 02", "missing stop byte")]
    [Arguments("18 05 61 00", "binary longer than remaining data")]
    [Arguments("19 F5 FF FF FF 7F 00", "list claiming more elements than bytes")]
    [Arguments("19 F5 FF FF FF FF 0F 00", "list size above int range")]
    [Arguments("1B 80 80 80 80 0F 85 00", "map claiming more entries than bytes")]
    [Arguments("15 80 80 80 80 80 80", "varint longer than 5 bytes")]
    [Arguments("1D 00", "invalid field type 13")]
    [Arguments("10 00", "field type 0 with non-zero delta")]
    [Arguments("19 0D", "invalid list element type")]
    public async Task MalformedInput_ThrowsParquetFormatException(string hex, string reason)
    {
        var data = TestBytes.Hex(hex);

        await Assert.That(() => SkipWholeStruct(data)).Throws<ParquetFormatException>();
        _ = reason; // documents each vector in the test report
    }

    [Test]
    public async Task ExcessiveStructNesting_Throws()
    {
        var data = TestBytes.Hex(string.Concat(Enumerable.Repeat("1C ", 100)));

        await Assert.That(() => SkipWholeStruct(data)).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task ExcessiveListNesting_Throws()
    {
        var data = TestBytes.Hex(string.Concat(Enumerable.Repeat("19 19 ", 100)));

        await Assert.That(() => SkipWholeStruct(data)).Throws<ParquetFormatException>();
    }

    private static int SkipWholeStruct(byte[] data) =>
        TestBytes.Read(data, (ref ThriftCompactReader r) =>
        {
            r.BeginStruct();
            while (r.TryReadField(out _, out var type))
            {
                r.Skip(type);
            }

            r.EndStruct();
            return r.Remaining;
        });
}
