using ParquetNeo.Geospatial;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Geospatial;

/// <summary>The bounding box of a geography: edges are great circle arcs, the box can cross the antimeridian, and a polygon around a pole reaches it.</summary>
public class GeographyBoundsTests
{
    private static BoundingBox? BoxOf(LogicalType type, params byte[][] values)
    {
        var collector = new GeospatialCollector(type);
        foreach (var value in values)
        {
            collector.Add(value);
        }

        return collector.Build()?.BoundingBox;
    }

    private static BoundingBox? Spherical(params byte[][] values) => BoxOf(LogicalType.Geography(), values);

    [Test]
    public async Task FeaturesThatStayOnOneSide_HaveTheBoxOfTheirVertices()
    {
        var box = Spherical(WkbBuilder.Point(10, 20), WkbBuilder.Point(30, -5), WkbBuilder.Point(25, 40))!;

        await Assert.That(box.XMin).IsEqualTo(10);
        await Assert.That(box.XMax).IsEqualTo(30);
        await Assert.That(box.YMin).IsEqualTo(-5);
        await Assert.That(box.YMax).IsEqualTo(40);
        await Assert.That(box.CrossesAntimeridian).IsFalse();
    }

    [Test]
    public async Task FeaturesAcrossTheAntimeridian_HaveABoxThatCrossesIt()
    {
        var box = Spherical(WkbBuilder.Point(170, 10), WkbBuilder.Point(-175, 20), WkbBuilder.Point(180, 0))!;

        await Assert.That(box.XMin).IsEqualTo(170);
        await Assert.That(box.XMax).IsEqualTo(-175);
        await Assert.That(box.CrossesAntimeridian).IsTrue();
    }

    [Test]
    public async Task AnEdgeAcrossTheAntimeridian_IsTheShortWay_NotThroughZero()
    {
        var box = Spherical(WkbBuilder.LineString([(170, 0), (-170, 0)]))!;

        await Assert.That(box.XMin).IsEqualTo(170);
        await Assert.That(box.XMax).IsEqualTo(-170);
    }

    [Test]
    public async Task AnEdgeBetweenTwoPointsOfTheSameLatitude_BulgesToThePole()
    {
        // The shortest way from 0 to 90 degrees of longitude at 60 north goes further north than 60.
        var box = Spherical(WkbBuilder.LineString([(0, 60), (90, 60)]))!;

        await Assert.That(box.YMin).IsEqualTo(60);
        await Assert.That(box.YMax).IsGreaterThan(60);
        await Assert.That(box.YMax).IsLessThan(90);
    }

    [Test]
    public async Task AnEdgeInTheSouth_BulgesSouth()
    {
        var box = Spherical(WkbBuilder.LineString([(-120, -30), (-20, -30)]))!;

        await Assert.That(box.YMax).IsEqualTo(-30);
        await Assert.That(box.YMin).IsLessThan(-30);
    }

    [Test]
    public async Task AnEdgeAlongTheEquator_StaysOnIt()
    {
        var box = Spherical(WkbBuilder.LineString([(10, 0), (80, 0)]))!;

        await Assert.That(box.YMin).IsEqualTo(0);
        await Assert.That(box.YMax).IsEqualTo(0);
    }

    [Test]
    public async Task AnEdgeOverAPole_HoldsEveryLongitude_AndBothPoles()
    {
        var box = Spherical(WkbBuilder.LineString([(0, 80), (180, 80)]))!;

        await Assert.That(box.XMin).IsEqualTo(-180);
        await Assert.That(box.XMax).IsEqualTo(180);
        await Assert.That(box.YMax).IsEqualTo(90);
    }

    [Test]
    public async Task APolygonAroundAPole_ReachesIt()
    {
        var ring = new[] { (-180d, 80d), (-90d, 80d), (0d, 80d), (90d, 80d), (180d, 80d) };

        var box = Spherical(WkbBuilder.Polygon([.. ring, ring[0]]))!;

        await Assert.That(box.YMax).IsEqualTo(90);
        await Assert.That(box.XMin).IsEqualTo(-180);
        await Assert.That(box.XMax).IsEqualTo(180);
    }

    [Test]
    public async Task APolygonAroundTheSouthPole_ReachesIt()
    {
        var ring = new[] { (-170d, -70d), (-80d, -70d), (10d, -70d), (100d, -70d), (170d, -70d), (-170d, -70d) };

        var box = Spherical(WkbBuilder.Polygon(ring))!;

        await Assert.That(box.YMin).IsEqualTo(-90);
    }

    [Test]
    public async Task ACoordinateOutOfRange_LeavesTheBoxOutButKeepsTheTypes()
    {
        var collector = new GeospatialCollector(LogicalType.Geography());
        collector.Add(WkbBuilder.Point(200, 10));
        collector.Add(WkbBuilder.Point(1, 95));

        var statistics = collector.Build()!;

        await Assert.That(statistics.BoundingBox).IsNull();
        await Assert.That(statistics.GeospatialTypes!.SequenceEqual([1])).IsTrue();
    }

    [Test]
    [Arguments(EdgeInterpolationAlgorithm.Vincenty)]
    [Arguments(EdgeInterpolationAlgorithm.Thomas)]
    [Arguments(EdgeInterpolationAlgorithm.Andoyer)]
    [Arguments(EdgeInterpolationAlgorithm.Karney)]
    public async Task AnotherWayOfDrawingEdges_HasNoBox_ButTheTypes(EdgeInterpolationAlgorithm algorithm)
    {
        var collector = new GeospatialCollector(LogicalType.Geography(algorithm: algorithm));
        collector.Add(WkbBuilder.LineString([(0, 0), (10, 10)]));

        var statistics = collector.Build()!;

        await Assert.That(statistics.BoundingBox).IsNull();
        await Assert.That(statistics.GeospatialTypes!.SequenceEqual([2])).IsTrue();
    }

    [Test]
    public async Task SphericalGivenOutright_IsTheDefault()
    {
        var box = BoxOf(LogicalType.Geography(algorithm: EdgeInterpolationAlgorithm.Spherical), WkbBuilder.LineString([(0, 60), (90, 60)]));

        await Assert.That(box).IsNotNull();
        await Assert.That(box!.YMax).IsGreaterThan(60);
    }

    [Test]
    public async Task ThePlanarBoxOfAGeometry_IsNotBulged_AndDoesNotWrap()
    {
        var box = BoxOf(LogicalType.Geometry(), WkbBuilder.LineString([(0, 60), (90, 60)]), WkbBuilder.Point(170, 0), WkbBuilder.Point(-170, 0))!;

        await Assert.That(box.YMax).IsEqualTo(60);
        await Assert.That(box.XMin).IsEqualTo(-170);
        await Assert.That(box.XMax).IsEqualTo(170);
    }

    /// <summary>
    /// The box holds every point of every edge: 2000 random edges are walked along their great circle, and every point is checked
    /// to be in the box of its edge (longitudes in the stretch from the west end to the east end, which can wrap).
    /// </summary>
    [Test]
    public async Task TheBoxOfAnEdge_HoldsEveryPointOfIt()
    {
        var random = new Random(1234);
        int checkedPoints = 0;
        for (int i = 0; i < 2000; i++)
        {
            double lon1 = (random.NextDouble() * 360) - 180, lat1 = (random.NextDouble() * 170) - 85;
            double lon2 = (random.NextDouble() * 360) - 180, lat2 = (random.NextDouble() * 170) - 85;
            var box = Spherical(WkbBuilder.LineString([(lon1, lat1), (lon2, lat2)]))!;

            var (a, b) = (ToVector(lon1, lat1), ToVector(lon2, lat2));
            double angle = Math.Acos(Math.Clamp((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z), -1, 1));
            if (angle < 1e-9 || Math.PI - angle < 1e-3)
            {
                continue; // the same point, or nearly opposite ones, whose short way is not well defined
            }

            for (int step = 0; step <= 200; step++)
            {
                double t = step / 200d;
                double w1 = Math.Sin((1 - t) * angle) / Math.Sin(angle), w2 = Math.Sin(t * angle) / Math.Sin(angle);
                double x = (w1 * a.X) + (w2 * b.X), y = (w1 * a.Y) + (w2 * b.Y), z = (w1 * a.Z) + (w2 * b.Z);
                double lat = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI;
                double lon = Math.Atan2(y, x) * 180 / Math.PI;

                bool latitudeIn = lat >= box.YMin - 1e-9 && lat <= box.YMax + 1e-9;
                bool longitudeIn = box.XMin <= box.XMax
                    ? lon >= box.XMin - 1e-9 && lon <= box.XMax + 1e-9
                    : lon >= box.XMin - 1e-9 || lon <= box.XMax + 1e-9;
                if (!latitudeIn || !longitudeIn)
                {
                    throw new InvalidOperationException($"Edge ({lon1}, {lat1}) to ({lon2}, {lat2}): the point ({lon}, {lat}) is outside the box [{box.XMin} {box.YMin}, {box.XMax} {box.YMax}].");
                }

                checkedPoints++;
            }
        }

        await Assert.That(checkedPoints).IsGreaterThan(300_000);
    }

    private static (double X, double Y, double Z) ToVector(double lon, double lat)
    {
        double lo = lon * Math.PI / 180, la = lat * Math.PI / 180;
        return (Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
    }
}
