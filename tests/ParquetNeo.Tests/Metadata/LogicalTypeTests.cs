using ParquetNeo.Metadata;
using ParquetNeo.Thrift;

namespace ParquetNeo.Tests.Metadata;

public class LogicalTypeTests
{
    [Test]
    public async Task UnrecognizedUnionMember_YieldsNoLogicalTypeAndKeepsTheRestOfTheElement()
    {
        // logicalType = union { 16 (VARIANT): struct {1: i8 1}, 99: struct {} } followed by more fields.
        var bytes = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteStringField(4, "variant_col");
            w.BeginStructField(10);
            w.BeginStructField(16);
            w.WriteI8Field(1, 1);
            w.EndStruct();
            w.EndStruct();
            w.WriteI32Field(11, 5);
            w.EndStruct();
        });

        var element = ThriftStruct.Deserialize<SchemaElement>(bytes, out int consumed);

        await Assert.That(element.Name).IsEqualTo("variant_col");
        await Assert.That(element.LogicalType).IsNull();
        await Assert.That(consumed).IsEqualTo(bytes.Length);
    }

    [Test]
    public async Task UnrecognizedLogicalType_IsOmittedWhenWriting()
    {
        var element = new SchemaElement { Name = "x", LogicalType = LogicalType.Unrecognized };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        ThriftStruct.Serialize(element, buffer);

        var parsed = ThriftStruct.Deserialize<SchemaElement>(buffer.WrittenSpan, out _);

        await Assert.That(parsed.Name).IsEqualTo("x");
        await Assert.That(parsed.LogicalType).IsNull();
    }

    [Test]
    public async Task Timestamp_EncodesUtcFlagAndUnit()
    {
        var element = new SchemaElement { Name = "t", LogicalType = LogicalType.Timestamp(true, TimeUnit.Micros) };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        ThriftStruct.Serialize(element, buffer);

        // 4: "t" | 10: union { 8: struct { 1: true, 2: union { 2: struct {} } } } | stop
        //   48 01 74 | 6C 8C 11 1C 2C 00(MicroSeconds) 00(TimeUnit) 00(Timestamp) 00(LogicalType) 00(element)
        await Assert.That(TestBytes.ToHex(buffer.WrittenSpan)).IsEqualTo(TestBytes.Normalize("48 01 74 6C 8C 11 1C 2C 00 00 00 00 00"));
    }

    [Test]
    public async Task Factories_SetOnlyTheirParameters()
    {
        var decimalType = LogicalType.Decimal(scale: 2, precision: 10);
        var integer = LogicalType.Integer(16, isSigned: false);

        await Assert.That(decimalType.Kind).IsEqualTo(LogicalTypeKind.Decimal);
        await Assert.That(decimalType.Scale).IsEqualTo(2);
        await Assert.That(decimalType.Precision).IsEqualTo(10);
        await Assert.That(integer.BitWidth).IsEqualTo(16);
        await Assert.That(integer.IsSigned).IsFalse();
        await Assert.That(LogicalType.Date).IsEqualTo(LogicalType.Date);
    }
}
