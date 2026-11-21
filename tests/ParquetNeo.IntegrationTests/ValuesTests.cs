using System.Text.Json;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.IntegrationTests;

/// <summary>
/// The comparison the other tests rely on, tried without Python: it must say that values differ when they do, or the tests would pass
/// whatever the files held. These run always.
/// </summary>
public class ValuesTests
{
    private static readonly PrimitiveNode Plain = new("c", PhysicalType.Int64, FieldRepetitionType.Optional);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Test]
    public async Task EqualValues_Match()
    {
        await Assert.That(Values.Compare(Json("5"), 5, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("-9223372036854775808"), long.MinValue, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("18446744073709551615"), ulong.MaxValue, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("1.5"), 1.5f, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("\"NaN\""), double.NaN, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("\"-Infinity\""), float.NegativeInfinity, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("true"), true, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("\"é\""), "é", Plain)).IsNull();
        await Assert.That(Values.Compare(Json("\"00ff\""), new byte[] { 0, 255 }, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("null"), null, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("null"), DBNull.Value, Plain)).IsNull();
    }

    [Test]
    public async Task ValuesThatDiffer_AreReported()
    {
        await Assert.That(Values.Compare(Json("5"), 6, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("5"), "5", Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("1.5"), 1.25, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("\"NaN\""), 1.0, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("1.0"), double.NaN, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("true"), false, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("\"a\""), "b", Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("\"00ff\""), new byte[] { 0, 254 }, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("null"), 0, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("0"), null, Plain)).IsNotNull();
        await Assert.That(Values.Compare(Json("18446744073709551615"), 18446744073709551614UL, Plain)).IsNotNull();
    }

    [Test]
    public async Task TheValuesOfTimes_AreTheirCountOfTheUnitOfTheColumn()
    {
        var millis = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Optional, LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Millis));
        var nanos = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Optional, LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Nanos));
        var moment = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);

        await Assert.That(Values.Compare(Json("1700000000000"), moment, millis)).IsNull();
        await Assert.That(Values.Compare(Json("1700000000000000000"), moment, nanos)).IsNull();
        await Assert.That(Values.Compare(Json("1700000000001"), moment, millis)).IsNotNull();
        await Assert.That(Values.Compare(Json("19675"), new DateOnly(2023, 11, 14), Plain)).IsNull();
        await Assert.That(Values.Compare(Json("19676"), new DateOnly(2023, 11, 14), Plain)).IsNotNull();
    }

    [Test]
    public async Task ADecimalIsItsTextWithTheScaleOfTheColumn()
    {
        var money = new PrimitiveNode("d", PhysicalType.Int64, FieldRepetitionType.Optional, LogicalType.Decimal(scale: 2, precision: 10));

        await Assert.That(Values.Compare(Json("\"1.50\""), 1.5m, money)).IsNull();
        await Assert.That(Values.Compare(Json("\"-0.07\""), -0.07m, money)).IsNull();
        await Assert.That(Values.Compare(Json("\"1.50\""), 1.51m, money)).IsNotNull();
    }

    [Test]
    public async Task AGuidIsTheHexOfItsText()
    {
        var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        await Assert.That(Values.Compare(Json("\"00112233445566778899aabbccddeeff\""), guid, Plain)).IsNull();
        await Assert.That(Values.Compare(Json("\"33221100554477668899aabbccddeeff\""), guid, Plain)).IsNotNull(); // the bytes of the Guid in .NET order
    }

    [Test]
    public async Task JsonValues_AreComparedByTheirValue()
    {
        await Assert.That(Values.Compare(Json("""{"a":[1,2,{"b":null}],"c":"x"}"""), Json("""{"c":"x","a":[1,2.0,{"b":null}]}"""))).IsNull();
        await Assert.That(Values.Compare(Json("""{"a":[1,2]}"""), Json("""{"a":[1,3]}"""))).IsNotNull();
        await Assert.That(Values.Compare(Json("""{"a":1}"""), Json("""{"a":1,"b":2}"""))).IsNotNull();
        await Assert.That(Values.Compare(Json("[1,2]"), Json("[1,2,3]"))).IsNotNull();
        await Assert.That(Values.Compare(Json("null"), Json("[]"))).IsNotNull();
        await Assert.That(Values.Compare(Json("true"), Json("false"))).IsNotNull();
        await Assert.That(Values.Compare(Json("\"a\""), Json("\"a\""))).IsNull();
    }
}
