using ParquetNeo.Data;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Geospatial;

/// <summary>A geometry or geography column is read through ADO.NET as the bytes of its Well-Known Binary.</summary>
public class GeospatialDataReaderTests
{
    [Test]
    public async Task AGeometryColumnAndAGeographyColumn_AreReadAsByteArrays()
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("id", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("shape", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.Geometry("EPSG:3857")),
            new PrimitiveNode("region", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.Geography()),
        ]);
        var stream = new MemoryStream();
        byte[][] shapes = [WkbBuilder.Point(1, 2), WkbBuilder.Point(3, 4)];
        using (var writer = ParquetFileWriter.Create(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(new[] { 1, 2, 3 });
            group.NextColumn().WriteByteArrays([shapes[0], shapes[1]], new[] { 1, 0, 1 });
            group.NextColumn().WriteByteArrays([shapes[1], shapes[0], shapes[1]]);
        }

        using var reader = ParquetDataReader.Open(new MemoryStream(stream.ToArray()));

        await Assert.That(reader.GetFieldType(1)).IsEqualTo(typeof(byte[]));
        await Assert.That(reader.GetFieldType(2)).IsEqualTo(typeof(byte[]));
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(((byte[])reader.GetValue(1)).SequenceEqual(shapes[0])).IsTrue();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.IsDBNull(1)).IsTrue();
        await Assert.That(((byte[])reader.GetValue(2)).SequenceEqual(shapes[0])).IsTrue();
    }
}
