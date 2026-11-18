using ParquetNeo.DataModel;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Tests.Generated;
using ParquetNeo.Tests;

namespace ParquetNeo.Serialization.Tests;

/// <summary>Byte array properties that hold Well-Known Binary, annotated as geometries and geographies.</summary>
public class GeospatialSerializerTests
{
    /// <summary>The same properties as <see cref="GeoPlace"/>, without a generated mapping: reflection handles it.</summary>
    private sealed class ReflectedPlace
    {
        public int Id { get; set; }

        [ParquetGeometry]
        public byte[]? Shape { get; set; }

        [ParquetGeometry(Crs = "EPSG:3857")]
        public byte[] Projected { get; set; } = [];

        [ParquetGeography]
        public byte[]? Region { get; set; }

        [ParquetGeography(Crs = "EPSG:4326", Algorithm = EdgeInterpolationAlgorithm.Vincenty)]
        public byte[]? Exact { get; set; }

        public List<byte[]>? Plain { get; set; }
    }

    private static ReflectedPlace[] Places(int count, int firstId = 0) =>
        [.. Enumerable.Range(firstId, count).Select(i => new ReflectedPlace
        {
            Id = i,
            Shape = i % 4 == 0 ? null : WkbBuilder.Point(i, i / 2d),
            Projected = WkbBuilder.LineString([(i * 1000, 0), (i * 1000, 500)]),
            Region = i % 5 == 0 ? null : WkbBuilder.Point(i % 180, 10),
            Exact = WkbBuilder.Point(1, 1),
            Plain = i % 2 == 0 ? null : [[1, 2, 3]],
        })];

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, int rowGroupSize = 100_000)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    [Test]
    public async Task TheAttributes_AnnotateTheColumns_WithTheirCrsAndAlgorithm()
    {
        var columns = ParquetSerializer.GetSchema<ReflectedPlace>().Columns.ToDictionary(c => c.Path[0]);

        await Assert.That(columns["Shape"].LogicalType).IsEqualTo(LogicalType.Geometry());
        await Assert.That(columns["Projected"].LogicalType).IsEqualTo(LogicalType.Geometry("EPSG:3857"));
        await Assert.That(columns["Region"].LogicalType).IsEqualTo(LogicalType.Geography());
        await Assert.That(columns["Exact"].LogicalType).IsEqualTo(LogicalType.Geography("EPSG:4326", EdgeInterpolationAlgorithm.Vincenty));
        await Assert.That(columns["Plain"].LogicalType).IsNull();
        await Assert.That(columns["Shape"].Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(columns["Projected"].Repetition).IsEqualTo(FieldRepetitionType.Required);
    }

    [Test]
    public async Task TheRows_RoundTrip_WithTheirBytes()
    {
        var rows = Places(30);

        var read = ParquetSerializer.Deserialize<ReflectedPlace>(new MemoryStream(Serialize(rows)));

        await Assert.That(read.Count).IsEqualTo(30);
        for (int i = 0; i < rows.Length; i++)
        {
            await Assert.That(read[i].Shape?.SequenceEqual(rows[i].Shape ?? [])).IsEqualTo(rows[i].Shape is null ? null : true);
            await Assert.That(read[i].Projected.SequenceEqual(rows[i].Projected)).IsTrue();
            await Assert.That(read[i].Region?.SequenceEqual(rows[i].Region ?? [])).IsEqualTo(rows[i].Region is null ? null : true);
        }
    }

    [Test]
    public async Task AGeneratedMapping_WritesTheSameFileAndReadsTheSameRows()
    {
        var reflected = Places(40);
        var generated = reflected.Select(p => new GeoPlace { Id = p.Id, Shape = p.Shape, Projected = p.Projected, Region = p.Region, Exact = p.Exact, Plain = p.Plain }).ToArray();

        var bytesOfReflection = Serialize(reflected, rowGroupSize: 10);
        var bytesOfGeneration = Serialize(generated, rowGroupSize: 10);
        var back = ParquetSerializer.Deserialize<GeoPlace>(new MemoryStream(bytesOfGeneration));

        await Assert.That(bytesOfGeneration.AsSpan().SequenceEqual(bytesOfReflection)).IsTrue();
        await Assert.That(back.Select(p => p.Id).SequenceEqual(reflected.Select(p => p.Id))).IsTrue();
        await Assert.That(back[7].Projected.SequenceEqual(reflected[7].Projected)).IsTrue();
    }

    [Test]
    public async Task TheFooter_HasTheBoxOfEachRowGroup_AndAFilterSkipsTheOthers()
    {
        // Rows 0 to 99 are points from (0, 0) to (99, 49); in row groups of 20 each box is a stretch of them.
        var bytes = Serialize(Places(100, firstId: 1), rowGroupSize: 20);
        using var file = ParquetFileReader.Open(new MemoryStream(bytes));

        var third = file.OpenRowGroup(2).GetGeospatialStatistics("Shape")!;
        var matching = file.GetMatchingRowGroups(ParquetFilter.Column("Shape").IntersectsBox(45, 20, 50, 30));
        var rows = ParquetSerializer.Deserialize<ReflectedPlace>(
            new MemoryStream(bytes),
            new ParquetDeserializerOptions { RowGroupFilter = ParquetFilter.Column("Shape").IntersectsBox(45, 20, 50, 30) });

        await Assert.That(third.BoundingBox!.XMin).IsGreaterThanOrEqualTo(41);
        await Assert.That(third.BoundingBox.XMax).IsLessThanOrEqualTo(60);
        await Assert.That(matching.SequenceEqual([2])).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(20);
        await Assert.That(rows.Any(r => r.Id is >= 45 and <= 50)).IsTrue();
    }

    [Test]
    public async Task AnAttributeOnAPropertyThatIsNotAByteArray_IsRefused()
    {
        await Assert.That(() => ParquetSerializer.GetSchema<GeometryOnText>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<GeographyOnNumber>()).Throws<InvalidOperationException>();
    }

    private sealed class GeometryOnText
    {
        [ParquetGeometry]
        public string Shape { get; set; } = "";
    }

    private sealed class GeographyOnNumber
    {
        [ParquetGeography]
        public int Shape { get; set; }
    }
}
