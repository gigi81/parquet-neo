using ParquetNeo.Compression;
using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Serialization.Generated;

namespace ParquetNeo.Tests.Serialization;

/// <summary>Objects, structs, collections and dictionaries through a generated mapping do what reflection does: same schema, same file, same rows, same errors.</summary>
public class GeneratedNestedTests
{
    private static void Fill(NestedBase row, int seed)
    {
        bool sparse = seed % 3 == 0;
        row.Id = seed;
        row.Home = new Place { Street = $"street {seed}", City = sparse ? null : $"city {seed}", Zip = seed * 11, Notes = sparse ? null : [$"n{seed}", $"m{seed}"] };
        row.Work = sparse ? null : new Place { Street = "work", Zip = seed, Notes = [] };
        row.Where = new Point { X = seed, Y = sparse ? null : seed + 1 };
        row.Spot = sparse ? null : new Point { X = -seed, Y = null };
        row.Tags = sparse ? [] : Enumerable.Range(0, seed % 4 + 1).Select(i => $"tag{seed}.{i}").ToList();
        row.MaybeTags = sparse ? null : [];
        row.Codes = sparse ? [] : [$"c{seed}", "x"];
        row.Sparse = ["a", null, $"b{seed}"];
        row.Scores = [1, null, seed];
        row.Amounts = [seed + 0.25m, 3m];
        row.ReadOnly = [seed, seed + 1];
        row.Labels = sparse ? [] : ["p", $"q{seed}"];
        row.Numbers = new HashSet<int> { seed, seed + 100 };
        row.Team = sparse ? [] : ["ada", null!, "alan"];
        row.Previous = sparse ? [] : [new Place { Street = "old", Zip = 1 }, new Place { Street = "older", City = "x", Zip = 2, Notes = ["z"] }];
        row.Many = sparse ? [] : [new Place { Street = "m", Zip = 3 }, null];
        row.Lines = sparse
            ? []
            : [new Line { Sku = $"s{seed}", Price = seed + 0.125m, Tier = Tier.Premium, Ship = new Place { Street = "ship", Zip = 9 } }, new Line { Sku = "t", Price = 1m, Tier = Tier.Basic }];
        row.Matrix = sparse ? [] : [[1, 2], [], [seed]];
        row.Counts = sparse ? new() : new() { ["a"] = 1, ["b"] = seed };
        row.Groups = sparse ? new Dictionary<int, List<string>>() : new Dictionary<int, List<string>> { [1] = ["x", "y"], [2] = [] };
        row.ByName = sparse ? new() : new() { ["home"] = new Place { Street = "h", Zip = 4 } };
        row.Optionals = new() { ["k"] = null, ["l"] = seed };
        row.Notes = new Dictionary<string, string?> { ["a"] = null, ["b"] = "bee" };
        row.Prices = new() { ["p"] = seed + 0.5m };
        row.Paths = sparse ? null : new() { ["route"] = [new Point { X = 1, Y = 2 }, new Point { X = 3 }] };
        row.Series = sparse ? [] : [new() { ["a"] = 1 }, new()];
    }

    private static List<NestedSink> Rows() => Enumerable.Range(0, 9).Select(i =>
    {
        var row = new NestedSink();
        Fill(row, i);
        return row;
    }).ToList();

    private static List<NestedTwin> Twins() => Enumerable.Range(0, 9).Select(i =>
    {
        var row = new NestedTwin();
        Fill(row, i);
        return row;
    }).ToList();

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, CompressionCodec codec = CompressionCodec.Uncompressed, int rowGroupSize = 100_000)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = codec }, RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    /// <summary>The columns and groups of the schema, in order, with what the file says of each.</summary>
    private static IEnumerable<string> Describe(SchemaNode node, string indent = "")
    {
        yield return $"{indent}{node.Name}|{node.Repetition}|{(node as PrimitiveNode)?.Type}|{node.LogicalType}|{(node as PrimitiveNode)?.TypeLength}";
        if (node is GroupNode group)
        {
            foreach (string line in group.Children.SelectMany(child => Describe(child, indent + " ")))
            {
                yield return line;
            }
        }
    }

    /// <summary>The same row as a <see cref="NestedSink"/>, so that rows read by the two paths can be compared.</summary>
    private static NestedSink Normalize(NestedBase row)
    {
        var copy = new NestedSink();
        foreach (var property in typeof(NestedBase).GetProperties().Where(p => p.SetMethod is not null))
        {
            property.SetValue(copy, property.GetValue(row));
        }

        return copy;
    }

    [Test]
    public async Task TheSchema_IsTheOneReflectionMakes_NodeForNode()
    {
        var generated = Describe(ParquetSerializer.GetSchema<NestedSink>().Root).ToArray();
        var reflected = Describe(ParquetSerializer.GetSchema<NestedTwin>().Root).ToArray();

        await Assert.That(generated).IsEquivalentTo(reflected);
        await Assert.That(generated.Length).IsGreaterThan(80);
    }

    [Test]
    [Arguments(CompressionCodec.Uncompressed, 100_000)]
    [Arguments(CompressionCodec.Gzip, 100_000)]
    [Arguments(CompressionCodec.Brotli, 4)]
    [Arguments(CompressionCodec.Uncompressed, 1)]
    public async Task TheFileWritten_IsByteForByteWhatReflectionWrites(CompressionCodec codec, int rowGroupSize)
    {
        byte[] generated = Serialize(Rows(), codec, rowGroupSize);
        byte[] reflected = Serialize(Twins(), codec, rowGroupSize);

        await Assert.That(generated.AsSpan().SequenceEqual(reflected)).IsTrue();
    }

    [Test]
    public async Task TheRowsRead_AreTheSameFromEitherPath_FromAFileOfEither()
    {
        foreach (byte[] bytes in new[] { Serialize(Rows()), Serialize(Twins()) })
        {
            var generated = ParquetSerializer.Deserialize<NestedSink>(new MemoryStream(bytes));
            var reflected = ParquetSerializer.Deserialize<NestedTwin>(new MemoryStream(bytes));

            await Assert.That(generated.Count).IsEqualTo(9);
            await Assert.That(reflected.Count).IsEqualTo(9);
            for (int i = 0; i < generated.Count; i++)
            {
                await Assert.That(DeepComparer.Difference(Normalize(reflected[i]), generated[i])).IsNull();
            }
        }
    }

    [Test]
    public async Task TheRowsRead_AreTheRowsWritten()
    {
        var expected = Rows();
        var read = ParquetSerializer.Deserialize<NestedSink>(new MemoryStream(Serialize(expected)));

        await Assert.That(read[1].Home.Street).IsEqualTo("street 1");
        await Assert.That(read[1].Work!.Street).IsEqualTo("work");
        await Assert.That(read[0].Work).IsNull();
        await Assert.That(read[1].Spot).IsEqualTo(new Point { X = -1, Y = null });
        await Assert.That(read[0].Spot).IsNull();
        await Assert.That(read[1].Lines[0].Ship!.Street).IsEqualTo("ship");
        await Assert.That(read[1].Lines[0].Tier).IsEqualTo(Tier.Premium);
        await Assert.That(read[1].Lines[0].Price).IsEqualTo(1.125m);
        await Assert.That(read[1].Matrix.Select(m => m.Count).ToArray()).IsEquivalentTo(new[] { 2, 0, 1 });
        await Assert.That(read[1].Groups[1]).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(read[1].Optionals["k"]).IsNull();
        await Assert.That(read[1].Paths!["route"][1].Y).IsNull();
        await Assert.That(read[1].Many[1]).IsNull();
        await Assert.That(read[1].Team.Count).IsEqualTo(3);
        await Assert.That(read[0].MaybeTags).IsNull();
        await Assert.That(read[1].MaybeTags).IsEmpty();
    }

    [Test]
    public async Task ARowOfOnlyDictionaries_AndOfOnlyANestedObject_RoundTrip()
    {
        var counters = new[] { new Counter { Name = "a", Counts = new() { ["x"] = 1 } }, new Counter { Name = "b" } };
        var read = ParquetSerializer.Deserialize<Counter>(new MemoryStream(Serialize(counters)));

        await Assert.That(read.Select(c => c.Name).ToArray()).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(read[0].Counts["x"]).IsEqualTo(1);
        await Assert.That(read[1].Counts).IsEmpty();
    }

    [Test]
    public async Task ANullInARequiredObject_IsRefusedByBothPathsWithTheSameMessage()
    {
        var generated = Assert.Throws<ArgumentException>(() => Serialize([new NestedSink { Home = null! }]));
        var reflected = Assert.Throws<ArgumentException>(() => Serialize([new NestedTwin { Home = null! }]));

        await Assert.That(generated.Message).IsEqualTo(reflected.Message);
        await Assert.That(generated.Message).Contains("Home");
    }

    [Test]
    public async Task ANullInARequiredCollection_AndInAnElementThatCannotBeNull_AreRefusedByBothPaths()
    {
        var nullCollection = new NestedSink { Tags = null! };
        var twinCollection = new NestedTwin { Tags = null! };
        var nullElement = new NestedSink { Tags = [null!] };
        var twinElement = new NestedTwin { Tags = [null!] };

        var generated = Assert.Throws<ArgumentException>(() => Serialize([nullCollection]));
        var reflected = Assert.Throws<ArgumentException>(() => Serialize([twinCollection]));
        var generatedElement = Assert.Throws<ArgumentException>(() => Serialize([nullElement]));
        var reflectedElement = Assert.Throws<ArgumentException>(() => Serialize([twinElement]));

        await Assert.That(generated.Message).IsEqualTo(reflected.Message);
        await Assert.That(generatedElement.Message).IsEqualTo(reflectedElement.Message);
    }

    [Test]
    public async Task ANullInAFileWhereTheRowCannotHoldOne_IsRefusedByBothPaths()
    {
        // A file whose Home is optional and null, read as a row whose Home is required.
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(new[] { new OptionalHome { Id = 1, Home = null } }, stream);
        byte[] bytes = stream.ToArray();

        var generated = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<HomeSink>(new MemoryStream(bytes)));
        var reflected = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<HomeTwin>(new MemoryStream(bytes)));

        await Assert.That(generated.Message).IsEqualTo(reflected.Message);
        await Assert.That(generated.Message).Contains("HomeBase.Home");
    }

    [Test]
    public async Task OnlyTheNestedColumnsTheRowTypeNames_AreReadFromAWiderFile()
    {
        var read = ParquetSerializer.Deserialize<JustTheStreet>(new MemoryStream(Serialize(Rows())));

        await Assert.That(read.Select(r => r.Home.Street).ToArray()).IsEquivalentTo(Enumerable.Range(0, 9).Select(i => $"street {i}").ToArray());
    }

    private sealed class OptionalHome
    {
        public int Id { get; set; }

        public Place? Home { get; set; }
    }

    private sealed class JustTheStreet
    {
        public StreetOnly Home { get; set; } = new();
    }

    private sealed class StreetOnly
    {
        public string Street { get; set; } = "";
    }
}
