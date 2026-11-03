using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Schema;

public class LogicalTypeRulesTests
{
    [Test]
    public async Task ToConvertedType_FollowsTheSpecTable()
    {
        (LogicalType? Logical, ConvertedType? Expected)[] cases =
        [
            (LogicalType.String, ConvertedType.Utf8),
            (LogicalType.Map, ConvertedType.Map),
            (LogicalType.List, ConvertedType.List),
            (LogicalType.Enum, ConvertedType.Enum),
            (LogicalType.Decimal(2, 9), ConvertedType.Decimal),
            (LogicalType.Date, ConvertedType.Date),
            (LogicalType.Json, ConvertedType.Json),
            (LogicalType.Bson, ConvertedType.Bson),
            // Local (not UTC adjusted) time still gets the legacy annotation; nanos have none.
            (LogicalType.Time(true, TimeUnit.Millis), ConvertedType.TimeMillis),
            (LogicalType.Time(false, TimeUnit.Micros), ConvertedType.TimeMicros),
            (LogicalType.Time(true, TimeUnit.Nanos), null),
            (LogicalType.Timestamp(true, TimeUnit.Millis), ConvertedType.TimestampMillis),
            (LogicalType.Timestamp(false, TimeUnit.Micros), ConvertedType.TimestampMicros),
            (LogicalType.Timestamp(false, TimeUnit.Nanos), null),
            (LogicalType.Integer(8, true), ConvertedType.Int8),
            (LogicalType.Integer(16, true), ConvertedType.Int16),
            (LogicalType.Integer(32, true), ConvertedType.Int32),
            (LogicalType.Integer(64, true), ConvertedType.Int64),
            (LogicalType.Integer(8, false), ConvertedType.UInt8),
            (LogicalType.Integer(16, false), ConvertedType.UInt16),
            (LogicalType.Integer(32, false), ConvertedType.UInt32),
            (LogicalType.Integer(64, false), ConvertedType.UInt64),
            (LogicalType.Integer(12, true), null),
            (LogicalType.Uuid, null),
            (LogicalType.Float16, null),
            (LogicalType.Null, null),
            (null, null),
        ];

        foreach (var (logical, expected) in cases)
        {
            await Assert.That(LogicalTypeRules.ToConvertedType(logical)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task FromConvertedType_FollowsTheSpecTable()
    {
        (ConvertedType Converted, LogicalType? Expected)[] cases =
        [
            (ConvertedType.Utf8, LogicalType.String),
            (ConvertedType.Map, LogicalType.Map),
            (ConvertedType.List, LogicalType.List),
            (ConvertedType.Enum, LogicalType.Enum),
            (ConvertedType.Date, LogicalType.Date),
            (ConvertedType.Json, LogicalType.Json),
            (ConvertedType.Bson, LogicalType.Bson),
            // The legacy time annotations always mean "UTC adjusted".
            (ConvertedType.TimeMillis, LogicalType.Time(true, TimeUnit.Millis)),
            (ConvertedType.TimeMicros, LogicalType.Time(true, TimeUnit.Micros)),
            (ConvertedType.TimestampMillis, LogicalType.Timestamp(true, TimeUnit.Millis)),
            (ConvertedType.TimestampMicros, LogicalType.Timestamp(true, TimeUnit.Micros)),
            (ConvertedType.Int8, LogicalType.Integer(8, true)),
            (ConvertedType.Int64, LogicalType.Integer(64, true)),
            (ConvertedType.UInt8, LogicalType.Integer(8, false)),
            (ConvertedType.UInt64, LogicalType.Integer(64, false)),
            (ConvertedType.MapKeyValue, null),
            (ConvertedType.Interval, null),
            ((ConvertedType)99, null),
        ];

        foreach (var (converted, expected) in cases)
        {
            await Assert.That(LogicalTypeRules.FromConvertedType(converted, null, null)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task FromConvertedType_Decimal_UsesSchemaElementScaleAndPrecision()
    {
        await Assert.That(LogicalTypeRules.FromConvertedType(ConvertedType.Decimal, 2, 9))
            .IsEqualTo(LogicalType.Decimal(2, 9));
        await Assert.That(LogicalTypeRules.FromConvertedType(ConvertedType.Decimal, null, 5))
            .IsEqualTo(LogicalType.Decimal(0, 5)); // the scale defaults to 0
        await Assert.That(LogicalTypeRules.FromConvertedType(ConvertedType.Decimal, 2, null)).IsNull(); // precision is required
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(2, 4)]
    [Arguments(4, 9)]
    [Arguments(8, 18)]
    [Arguments(16, 38)]
    public async Task MaxDecimalPrecision_MatchesTheSpecFormula(int byteLength, int expected)
    {
        await Assert.That(LogicalTypeRules.MaxDecimalPrecision(byteLength)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetError_AcceptsValidCombinations()
    {
        (LogicalType Logical, PhysicalType Physical, int? Length)[] valid =
        [
            (LogicalType.String, PhysicalType.ByteArray, null),
            (LogicalType.Enum, PhysicalType.ByteArray, null),
            (LogicalType.Json, PhysicalType.ByteArray, null),
            (LogicalType.Bson, PhysicalType.ByteArray, null),
            (LogicalType.Uuid, PhysicalType.FixedLenByteArray, 16),
            (LogicalType.Float16, PhysicalType.FixedLenByteArray, 2),
            (LogicalType.Date, PhysicalType.Int32, null),
            (LogicalType.Time(true, TimeUnit.Millis), PhysicalType.Int32, null),
            (LogicalType.Time(true, TimeUnit.Micros), PhysicalType.Int64, null),
            (LogicalType.Time(false, TimeUnit.Nanos), PhysicalType.Int64, null),
            (LogicalType.Timestamp(true, TimeUnit.Millis), PhysicalType.Int64, null),
            (LogicalType.Timestamp(true, TimeUnit.Nanos), PhysicalType.FixedLenByteArray, 12),
            (LogicalType.Integer(8, true), PhysicalType.Int32, null),
            (LogicalType.Integer(32, false), PhysicalType.Int32, null),
            (LogicalType.Integer(64, false), PhysicalType.Int64, null),
            (LogicalType.Decimal(0, 9), PhysicalType.Int32, null),
            (LogicalType.Decimal(5, 18), PhysicalType.Int64, null),
            (LogicalType.Decimal(10, 100), PhysicalType.ByteArray, null),
            (LogicalType.Decimal(2, 38), PhysicalType.FixedLenByteArray, 16),
            (LogicalType.Null, PhysicalType.Int32, null),
        ];

        foreach (var (logical, physical, length) in valid)
        {
            await Assert.That(LogicalTypeRules.GetError(logical, isGroup: false, physical, length)).IsNull();
        }
    }

    [Test]
    public async Task GetError_RejectsInvalidCombinations()
    {
        (LogicalType Logical, PhysicalType Physical, int? Length)[] invalid =
        [
            (LogicalType.String, PhysicalType.Int32, null),
            (LogicalType.Uuid, PhysicalType.FixedLenByteArray, 8),
            (LogicalType.Uuid, PhysicalType.ByteArray, null),
            (LogicalType.Float16, PhysicalType.FixedLenByteArray, 4),
            (LogicalType.Date, PhysicalType.Int64, null),
            (LogicalType.Time(true, TimeUnit.Millis), PhysicalType.Int64, null),
            (LogicalType.Time(true, TimeUnit.Micros), PhysicalType.Int32, null),
            (LogicalType.Timestamp(true, TimeUnit.Millis), PhysicalType.Int32, null),
            (LogicalType.Timestamp(true, TimeUnit.Micros), PhysicalType.FixedLenByteArray, 12),
            (LogicalType.Integer(8, true), PhysicalType.Int64, null),
            (LogicalType.Integer(64, true), PhysicalType.Int32, null),
            (LogicalType.Integer(12, true), PhysicalType.Int32, null),
            (LogicalType.Decimal(0, 10), PhysicalType.Int32, null),
            (LogicalType.Decimal(0, 19), PhysicalType.Int64, null),
            (LogicalType.Decimal(0, 0), PhysicalType.ByteArray, null),
            (LogicalType.Decimal(5, 4), PhysicalType.Int32, null), // scale above precision
            (LogicalType.Decimal(-1, 4), PhysicalType.Int32, null),
            (LogicalType.Decimal(0, 5), PhysicalType.FixedLenByteArray, 2), // 2 bytes hold at most 4 digits
            (LogicalType.Decimal(0, 5), PhysicalType.Double, null),
            (LogicalType.Null, PhysicalType.Int96, null),
            (LogicalType.List, PhysicalType.Int32, null),
            (LogicalType.Map, PhysicalType.ByteArray, null),
            (LogicalType.Unrecognized, PhysicalType.Int32, null),
        ];

        foreach (var (logical, physical, length) in invalid)
        {
            await Assert.That(LogicalTypeRules.GetError(logical, isGroup: false, physical, length)).IsNotNull();
        }
    }

    [Test]
    public async Task GetError_OnGroupsOnlyAllowsListAndMap()
    {
        await Assert.That(LogicalTypeRules.GetError(LogicalType.List, isGroup: true, default, null)).IsNull();
        await Assert.That(LogicalTypeRules.GetError(LogicalType.Map, isGroup: true, default, null)).IsNull();
        await Assert.That(LogicalTypeRules.GetError(LogicalType.String, isGroup: true, default, null)).IsNotNull();
        await Assert.That(LogicalTypeRules.GetError(LogicalType.Decimal(0, 5), isGroup: true, default, null)).IsNotNull();
    }
}
