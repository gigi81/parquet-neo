using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Geospatial;

/// <summary>A condition on the extent of a geometry or geography column rules out the row groups whose bounding box does not meet it.</summary>
public class GeospatialFilterTests
{
    /// <summary>Four row groups of points in four quarters of the plane, the last of nulls only; an id column goes with them.</summary>
    private static ParquetFileReader FourQuarters(LogicalType type, bool statistics = true)
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("id", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("shape", PhysicalType.ByteArray, FieldRepetitionType.Optional, type),
        ]);
        var stream = new MemoryStream();
        using (var writer = ParquetFileWriter.Create(stream, schema, new ParquetWriterOptions { StatisticsEnabled = statistics }, leaveOpen: true))
        {
            (double X, double Y)[] origins = [(10, 10), (-60, 10), (10, -60), (0, 0)];
            for (int group = 0; group < 4; group++)
            {
                using var rowGroup = writer.AppendRowGroup();
                rowGroup.NextColumn().WriteBatch(new[] { group * 10, (group * 10) + 1, (group * 10) + 2 });
                if (group == 3)
                {
                    rowGroup.NextColumn().WriteByteArrays(ReadOnlySpan<ReadOnlyMemory<byte>>.Empty, new[] { 0, 0, 0 });
                    continue;
                }

                var (x, y) = origins[group];
                rowGroup.NextColumn().WriteByteArrays(
                    new[] { WkbBuilder.Point(x, y), WkbBuilder.Point(x + 5, y + 5), WkbBuilder.Point(x + 10, y + 10) }.Select(v => (ReadOnlyMemory<byte>)v).ToArray(),
                    new[] { 1, 1, 1 });
            }
        }

        return ParquetFileReader.Open(new MemoryStream(stream.ToArray()));
    }

    private static int[] Matching(ParquetFileReader file, ParquetFilter filter) => [.. file.GetMatchingRowGroups(filter)];

    [Test]
    public async Task ARectangle_KeepsTheRowGroupsWhoseBoxMeetsIt()
    {
        using var file = FourQuarters(LogicalType.Geometry());
        var shape = ParquetFilter.Column("shape");

        await Assert.That(Matching(file, shape.IntersectsBox(12, 12, 14, 14)).SequenceEqual([0])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(-100, -100, 100, 100)).SequenceEqual([0, 1, 2])).IsTrue(); // the group of nulls has no feature
        await Assert.That(Matching(file, shape.IntersectsBox(-55, 12, -50, 15)).SequenceEqual([1])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(1000, 1000, 2000, 2000))).IsEmpty();
    }

    [Test]
    public async Task ARectangleThatOnlyTouchesTheBox_StillMeetsIt()
    {
        using var file = FourQuarters(LogicalType.Geometry());

        // The first group's box is 10 to 20 on both axes.
        await Assert.That(Matching(file, ParquetFilter.Column("shape").IntersectsBox(20, 20, 30, 30)).SequenceEqual([0])).IsTrue();
        await Assert.That(Matching(file, ParquetFilter.Column("shape").IntersectsBox(20.0001, 20.0001, 30, 30))).IsEmpty();
    }

    [Test]
    public async Task TheConditionIsCombinedWithOthers_AndNegated()
    {
        using var file = FourQuarters(LogicalType.Geometry());
        var shape = ParquetFilter.Column("shape");
        var id = ParquetFilter.Column("id");

        await Assert.That(Matching(file, shape.IntersectsBox(-100, -100, 100, 100).And(id.GreaterThanOrEqual(10))).SequenceEqual([1, 2])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(12, 12, 14, 14).Or(id.Equal(21))).SequenceEqual([0, 2])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(12, 12, 14, 14).Not()).Count).IsEqualTo(4); // none of the others holds a feature in it, but the null rows and the rest are not ruled out
    }

    [Test]
    public async Task ARowGroupWithoutABox_IsKept_AndSoIsAColumnOfAnotherType()
    {
        using var withoutBox = FourQuarters(LogicalType.Geometry(), statistics: false);
        using var other = FourQuarters(LogicalType.Geometry());

        await Assert.That(Matching(withoutBox, ParquetFilter.Column("shape").IntersectsBox(1000, 1000, 2000, 2000)).Count).IsEqualTo(4);
        await Assert.That(Matching(other, ParquetFilter.Column("id").IntersectsBox(1000, 1000, 2000, 2000)).Count).IsEqualTo(4);
        await Assert.That(Matching(other, ParquetFilter.Column("nothing").IntersectsBox(1000, 1000, 2000, 2000)).Count).IsEqualTo(4);
    }

    [Test]
    public async Task AGeography_IsFilteredAcrossTheAntimeridian()
    {
        var schema = new ParquetSchema([new PrimitiveNode("shape", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.Geography())]);
        var stream = new MemoryStream();
        using (var writer = ParquetFileWriter.Create(stream, schema, null, leaveOpen: true))
        {
            using (var east = writer.AppendRowGroup())
            {
                east.NextColumn().WriteByteArrays([WkbBuilder.Point(170, 10), WkbBuilder.Point(-175, 12)]);
            }

            using (var europe = writer.AppendRowGroup())
            {
                europe.NextColumn().WriteByteArrays([WkbBuilder.Point(2, 48), WkbBuilder.Point(12, 41)]);
            }
        }

        using var file = ParquetFileReader.Open(new MemoryStream(stream.ToArray()));
        var shape = ParquetFilter.Column("shape");

        await Assert.That(Matching(file, shape.IntersectsBox(175, 0, 180, 20)).SequenceEqual([0])).IsTrue();      // inside the part before the antimeridian
        await Assert.That(Matching(file, shape.IntersectsBox(-180, 0, -176, 20)).SequenceEqual([0])).IsTrue();    // inside the part after it
        await Assert.That(Matching(file, shape.IntersectsBox(-170, 0, -160, 20))).IsEmpty();                       // in the gap between -175 and 170 the long way round
        await Assert.That(Matching(file, shape.IntersectsBox(0, 40, 20, 50)).SequenceEqual([1])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(160, 0, -170, 50)).SequenceEqual([0])).IsTrue();     // a rectangle that crosses it itself
        await Assert.That(Matching(file, shape.IntersectsBox(170, 0, -170, 50)).SequenceEqual([0])).IsTrue();
        await Assert.That(Matching(file, shape.IntersectsBox(100, 0, -100, 50)).SequenceEqual([0])).IsTrue();     // the longitudes from 100 east to -100: Europe is not among them
        await Assert.That(Matching(file, shape.IntersectsBox(-100, 0, 100, 50)).SequenceEqual([1])).IsTrue();     // and from -100 east to 100: the other group is not
    }

    [Test]
    public async Task TheFilter_ChecksItsArguments_AndHasATextAndColumns()
    {
        var filter = ParquetFilter.Column("shape").IntersectsBox(1, 2, 3, 4);

        await Assert.That(() => ParquetFilter.Column("shape").IntersectsBox(double.NaN, 0, 1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ParquetFilter.Column("shape").IntersectsBox(0, 0, double.NaN, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ParquetFilter.Column("shape").IntersectsBox(0, 5, 1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(filter.ToString()).IsEqualTo("shape intersects [1 2, 3 4]");
        await Assert.That(filter.Columns.Single().SequenceEqual(["shape"])).IsTrue();
    }

    [Test]
    public async Task ABoxIntersectsARectangle_ByTheRulesOfIntervals()
    {
        var box = new BoundingBox { XMin = 0, XMax = 10, YMin = 0, YMax = 10 };
        var wrapping = new BoundingBox { XMin = 170, XMax = -170, YMin = -10, YMax = 10 };

        await Assert.That(box.Intersects(5, 5, 6, 6)).IsTrue();
        await Assert.That(box.Intersects(11, 0, 12, 10)).IsFalse();
        await Assert.That(box.Intersects(0, 11, 10, 12)).IsFalse();
        await Assert.That(box.Intersects(-3, -3, 0, 0)).IsTrue();
        await Assert.That(wrapping.CrossesAntimeridian).IsTrue();
        await Assert.That(wrapping.Intersects(175, 0, 178, 1)).IsTrue();
        await Assert.That(wrapping.Intersects(-178, 0, -175, 1)).IsTrue();
        await Assert.That(wrapping.Intersects(0, 0, 10, 1)).IsFalse();
        await Assert.That(wrapping.Intersects(160, 0, -160, 1)).IsTrue();
        await Assert.That(box.Intersects(5, 0, -5, 1)).IsTrue();   // a rectangle that wraps takes in the antimeridian, and the ends are 5 and -5
        await Assert.That(box.Intersects(15, 0, -15, 1)).IsFalse();
        await Assert.That(wrapping.Intersects(100, -50, -100, -20)).IsFalse();
    }

    [Test]
    public async Task TheBoundingBoxAndTypes_SurviveTheFooter_WhenReadByARowGroupReader()
    {
        using var file = FourQuarters(LogicalType.Geometry());
        var group = file.OpenRowGroup(1);

        var byIndex = group.GetGeospatialStatistics(1)!;
        var byName = group.GetGeospatialStatistics("shape")!;

        await Assert.That(byIndex.BoundingBox!.XMin).IsEqualTo(-60);
        await Assert.That(byName.BoundingBox!.YMax).IsEqualTo(20);
        await Assert.That(group.GetGeospatialStatistics("id")).IsNull();
        await Assert.That(() => group.GetGeospatialStatistics(5)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => group.GetGeospatialStatistics("nope")).Throws<ArgumentException>();
        await Assert.That(() => group.GetGeospatialStatistics(null!)).Throws<ArgumentNullException>();
    }
}
