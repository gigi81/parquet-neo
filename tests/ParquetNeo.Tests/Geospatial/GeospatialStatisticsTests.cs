using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Geospatial;

/// <summary>The bounding box and the types of the features of a geometry or geography column, as the writer records them in the footer.</summary>
public class GeospatialStatisticsTests
{
    private static ParquetSchema SchemaOf(LogicalType type, FieldRepetitionType repetition = FieldRepetitionType.Optional) =>
        new([new PrimitiveNode("shape", PhysicalType.ByteArray, repetition, type)]);

    /// <summary>Writes one row group of the values (a null is a null row) and gives back the reader of the file.</summary>
    private static ParquetFileReader Write(LogicalType type, ParquetWriterOptions? options, byte[]?[] values) => WriteGroups(type, options, [values]);

    private static ParquetFileReader WriteGroups(LogicalType type, ParquetWriterOptions? options, byte[]?[][] groups)
    {
        var stream = new MemoryStream();
        using (var writer = ParquetFileWriter.Create(stream, SchemaOf(type), options, leaveOpen: true))
        {
            foreach (var values in groups)
            {
                using var group = writer.AppendRowGroup();
                var present = values.Where(v => v is not null).Select(v => (ReadOnlyMemory<byte>)v!).ToArray();
                var levels = values.Select(v => v is null ? 0 : 1).ToArray();
                group.NextColumn().WriteByteArrays(present, levels);
            }
        }

        stream.Position = 0;
        return ParquetFileReader.Open(stream);
    }

    private static GeospatialStatistics? Stats(ParquetFileReader file, int group = 0) => file.OpenRowGroup(group).GetGeospatialStatistics("shape");

    [Test]
    public async Task AGeometryColumn_HasTheBoundingBoxAndTheTypesOfItsFeatures()
    {
        using var file = Write(LogicalType.Geometry(), null,
        [
            WkbBuilder.Point(1, 2),
            WkbBuilder.LineString([(-5, 0), (3, 10)]),
            WkbBuilder.Polygon(WkbBuilder.Rectangle(0, -1, 4, 7)),
            WkbBuilder.Point(0.5, 0.5, bigEndian: true),
            null,
        ]);

        var stats = Stats(file)!;

        await Assert.That(stats.BoundingBox!.XMin).IsEqualTo(-5);
        await Assert.That(stats.BoundingBox.XMax).IsEqualTo(4);
        await Assert.That(stats.BoundingBox.YMin).IsEqualTo(-1);
        await Assert.That(stats.BoundingBox.YMax).IsEqualTo(10);
        await Assert.That(stats.BoundingBox.ZMin).IsNull();
        await Assert.That(stats.BoundingBox.MMin).IsNull();
        await Assert.That(stats.GeospatialTypes!.SequenceEqual([1, 2, 3])).IsTrue();
    }

    [Test]
    public async Task TheZAndMOfTheCoordinates_AreBoundedAndTheirTypesHaveTheCodesOfIso()
    {
        using var file = Write(LogicalType.Geometry(), null,
        [
            WkbBuilder.Point(0, 0, z: 5, m: 100),
            WkbBuilder.Point(1, 1, z: -2, m: 50),
            WkbBuilder.Point(2, 2, z: 9),
            WkbBuilder.Point(3, 3, m: 7),
        ]);

        var stats = Stats(file)!;

        await Assert.That(stats.BoundingBox!.ZMin).IsEqualTo(-2);
        await Assert.That(stats.BoundingBox.ZMax).IsEqualTo(9);
        await Assert.That(stats.BoundingBox.MMin).IsEqualTo(7);
        await Assert.That(stats.BoundingBox.MMax).IsEqualTo(100);
        await Assert.That(stats.GeospatialTypes!.SequenceEqual([1001, 2001, 3001])).IsTrue();
    }

    [Test]
    public async Task CollectionsCountByTheirOwnTypeAndHoldTheirParts()
    {
        using var file = Write(LogicalType.Geometry(), null,
        [
            WkbBuilder.Collection(4, WkbBuilder.Point(10, 10), WkbBuilder.Point(20, -5)),
            WkbBuilder.Collection(7, WkbBuilder.Point(-3, 0), WkbBuilder.LineString([(0, 0), (0, 30)])),
        ]);

        var stats = Stats(file)!;

        await Assert.That(stats.GeospatialTypes!.SequenceEqual([4, 7])).IsTrue();
        await Assert.That(stats.BoundingBox!.XMin).IsEqualTo(-3);
        await Assert.That(stats.BoundingBox.XMax).IsEqualTo(20);
        await Assert.That(stats.BoundingBox.YMin).IsEqualTo(-5);
        await Assert.That(stats.BoundingBox.YMax).IsEqualTo(30);
    }

    [Test]
    public async Task EmptyPoints_AreIgnored_AndAColumnOfOnlyThemHasTypesButNoBox()
    {
        using var mixed = Write(LogicalType.Geometry(), null, [WkbBuilder.EmptyPoint(), WkbBuilder.Point(4, 5)]);
        using var empty = Write(LogicalType.Geometry(), null, [WkbBuilder.EmptyPoint(), WkbBuilder.EmptyPoint(bigEndian: true)]);

        await Assert.That(Stats(mixed)!.BoundingBox!.XMin).IsEqualTo(4);
        await Assert.That(Stats(empty)!.BoundingBox).IsNull();
        await Assert.That(Stats(empty)!.GeospatialTypes!.SequenceEqual([1])).IsTrue();
    }

    [Test]
    public async Task AColumnOfNulls_HasNoGeospatialStatistics()
    {
        using var file = Write(LogicalType.Geometry(), null, new byte[]?[] { null, null });

        await Assert.That(Stats(file)).IsNull();
    }

    [Test]
    public async Task AValueThatIsNotValidWkb_LeavesTheChunkWithoutStatistics_AndIsStoredAsItIs()
    {
        byte[][] bad =
        [
            [],
            [1],
            [1, 1, 0, 0, 0, 1, 2, 3],                                  // a point cut short
            [2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], // a byte order that is none
            [1, 99, 0, 0, 0],                                          // a type that is none
            [.. WkbBuilder.Point(1, 2), 0],                            // bytes left over
            [1, 2, 0, 0, 0, 255, 255, 255, 127],                       // a line with 2 billion points that are not there
        ];

        foreach (var value in bad)
        {
            using var file = Write(LogicalType.Geometry(), null, [WkbBuilder.Point(0, 0), value]);
            using var column = file.OpenRowGroup(0).OpenColumn(0);
            var read = new ReadOnlyMemory<byte>[2];

            await Assert.That(Stats(file)).IsNull();
            await Assert.That(column.ReadByteArrays(read, new int[2]).Values).IsEqualTo(2);
            await Assert.That(read[1].ToArray().SequenceEqual(value)).IsTrue();
        }
    }

    [Test]
    public async Task EachRowGroup_HasTheBoxOfItsOwnFeatures()
    {
        using var file = WriteGroups(LogicalType.Geometry(), null,
        [
            [WkbBuilder.Point(0, 0), WkbBuilder.Point(1, 1)],
            [WkbBuilder.Point(100, 50), WkbBuilder.Point(110, 60)],
        ]);

        await Assert.That(Stats(file, 0)!.BoundingBox!.XMax).IsEqualTo(1);
        await Assert.That(Stats(file, 1)!.BoundingBox!.XMin).IsEqualTo(100);
        await Assert.That(Stats(file, 1)!.BoundingBox!.YMax).IsEqualTo(60);
    }

    [Test]
    public async Task TheFeaturesThatAreRepeated_AreCountedOnceInADictionary_AndTheBoxIsTheSame()
    {
        var values = Enumerable.Range(0, 500).Select(i => (byte[]?)WkbBuilder.Point(i % 10, i % 7)).ToArray();

        using var dictionary = Write(LogicalType.Geometry(), new ParquetWriterOptions { DictionaryEnabled = true }, values);
        using var plain = Write(LogicalType.Geometry(), new ParquetWriterOptions { DictionaryEnabled = false }, values);

        await Assert.That(Stats(dictionary)!.BoundingBox!.XMax).IsEqualTo(9);
        await Assert.That(Stats(dictionary)!.BoundingBox!.YMax).IsEqualTo(6);
        await Assert.That(Stats(plain)!.BoundingBox!.XMax).IsEqualTo(9);
    }

    [Test]
    public async Task WithoutStatistics_NothingIsRecorded()
    {
        using var file = Write(LogicalType.Geometry(), new ParquetWriterOptions { StatisticsEnabled = false }, [WkbBuilder.Point(1, 1)]);

        await Assert.That(Stats(file)).IsNull();
    }

    [Test]
    public async Task AGeometryColumn_HasNoMinimumAndMaximum_AndTheNullCountIsThere()
    {
        using var file = Write(LogicalType.Geometry(), new ParquetWriterOptions { WritePageIndex = true, DataPageSize = 200 },
            [.. Enumerable.Range(0, 200).Select(i => i % 10 == 0 ? null : (byte[]?)WkbBuilder.Point(i, i)), null]);

        var statistics = file.OpenRowGroup(0).GetStatistics("shape")!;

        await Assert.That(statistics.Min).IsNull();
        await Assert.That(statistics.Max).IsNull();
        await Assert.That(statistics.NullCount).IsEqualTo(21);
        await Assert.That(Stats(file)!.BoundingBox!.XMax).IsEqualTo(199);
    }

    [Test]
    public async Task TheMinimumAndMaximumAnotherWriterRecorded_AreNotBelieved()
    {
        using var file = Write(LogicalType.Geometry(), null, [WkbBuilder.Point(1, 1)]);
        var chunk = file.OpenRowGroup(0).Metadata.Columns[0].MetaData!;
        chunk.Statistics = new ParquetNeo.Metadata.Statistics { MinValue = [1, 2, 3], MaxValue = [4, 5, 6], NullCount = 0 };

        var statistics = file.OpenRowGroup(0).GetStatistics("shape")!;

        await Assert.That(statistics.Min).IsNull();
        await Assert.That(statistics.Max).IsNull();
    }

    [Test]
    public async Task TheAnnotation_IsWrittenAndRead_WithItsCrsAndAlgorithm()
    {
        foreach (var type in new[]
        {
            LogicalType.Geometry(),
            LogicalType.Geometry("EPSG:3857"),
            LogicalType.Geography(),
            LogicalType.Geography("EPSG:4326", EdgeInterpolationAlgorithm.Karney),
            LogicalType.Geography(algorithm: EdgeInterpolationAlgorithm.Vincenty),
        })
        {
            using var file = Write(type, null, [WkbBuilder.Point(1, 1)]);

            var read = file.Schema.Columns[0].LogicalType!;

            await Assert.That(read).IsEqualTo(type);
        }
    }

    [Test]
    public async Task AnnotatingAColumnThatIsNotAByteArray_IsRefused()
    {
        await Assert.That(() => new PrimitiveNode("shape", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Geometry())).Throws<ArgumentException>();
        await Assert.That(() => new PrimitiveNode("shape", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, LogicalType.Geography(), typeLength: 16)).Throws<ArgumentException>();
    }

    [Test]
    public async Task APageIndexOfAGeometryColumn_HasNoBoundsOfTheValues()
    {
        var stream = new MemoryStream();
        using (var writer = ParquetFileWriter.Create(stream, SchemaOf(LogicalType.Geometry(), FieldRepetitionType.Required), new ParquetWriterOptions { WritePageIndex = true, DataPageSize = 300 }, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteByteArrays(Enumerable.Range(0, 100).Select(i => (ReadOnlyMemory<byte>)WkbBuilder.Point(i, i)).ToArray());
        }

        using var file = ParquetFileReader.Open(new MemoryStream(stream.ToArray()));
        var chunk = file.Metadata.RowGroups[0].Columns[0];

        await Assert.That(chunk.OffsetIndexOffset).IsNotNull();
        await Assert.That(file.OpenRowGroup(0).GetGeospatialStatistics(0)!.BoundingBox!.XMax).IsEqualTo(99);
    }
}
