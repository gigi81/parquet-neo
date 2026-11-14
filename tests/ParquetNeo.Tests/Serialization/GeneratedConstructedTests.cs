using ParquetNeo.Compression;
using ParquetNeo.DataModel;
using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Serialization.Generated;

namespace ParquetNeo.Tests.Serialization;

/// <summary>Types created by a constructor with parameters (records, for example) through a generated mapping do what reflection does.</summary>
public class GeneratedConstructedTests
{
    private static Citizen MakeCitizen(int i) => new(
        i,
        $"name {i}",
        new DateOnly(2000, 1, 1).AddDays(i),
        i % 3 == 0 ? null : new Place { Street = $"street {i}", Zip = i },
        new Spot(i, i % 2 == 0 ? null : i),
        i % 4 == 0 ? [] : [new Spot(1, 2), new Spot(3, null)],
        i % 2 == 0 ? new() : new() { ["a"] = new Spot(i, 1), ["b"] = null },
        new Coord(i, -i),
        i % 3 == 0 ? null : new Coord(7, 8),
        i % 5 == 0 ? [] : [$"t{i}", "x"]);

    private static CitizenTwin MakeTwin(int i)
    {
        var p = MakeCitizen(i);
        return new CitizenTwin(p.Id, p.Name, p.Born, p.Home, p.Where, p.Path, p.Named, p.Origin, p.Last, p.Tags);
    }

    private static Citizen ToCitizen(CitizenTwin t) => new(t.Id, t.Name, t.Born, t.Home, t.Where, t.Path, t.Named, t.Origin, t.Last, t.Tags);

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, CompressionCodec codec = CompressionCodec.Uncompressed, int rowGroupSize = 100_000)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = codec }, RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    private static IEnumerable<string> Describe(SchemaNode node, string indent = "")
    {
        yield return $"{indent}{node.Name}|{node.Repetition}|{(node as PrimitiveNode)?.Type}|{node.LogicalType}";
        if (node is GroupNode group)
        {
            foreach (string line in group.Children.SelectMany(child => Describe(child, indent + " ")))
            {
                yield return line;
            }
        }
    }

    [Test]
    public async Task TheSchemaAndTheFile_AreWhatReflectionMakes()
    {
        var generated = Describe(ParquetSerializer.GetSchema<Citizen>().Root).ToArray();
        var reflected = Describe(ParquetSerializer.GetSchema<CitizenTwin>().Root).ToArray();
        await Assert.That(generated).IsEquivalentTo(reflected);

        foreach (var (codec, size) in new[] { (CompressionCodec.Uncompressed, 100_000), (CompressionCodec.Gzip, 100_000), (CompressionCodec.Brotli, 3) })
        {
            byte[] fromGenerated = Serialize(Enumerable.Range(0, 20).Select(MakeCitizen), codec, size);
            byte[] fromReflection = Serialize(Enumerable.Range(0, 20).Select(MakeTwin), codec, size);
            await Assert.That(fromGenerated.AsSpan().SequenceEqual(fromReflection)).IsTrue();
        }
    }

    [Test]
    public async Task TheRowsRead_AreTheRowsWritten_ByEitherPath_FromAFileOfEither()
    {
        var expected = Enumerable.Range(0, 20).Select(MakeCitizen).ToList();
        foreach (byte[] bytes in new[] { Serialize(expected), Serialize(Enumerable.Range(0, 20).Select(MakeTwin)) })
        {
            var generated = ParquetSerializer.Deserialize<Citizen>(new MemoryStream(bytes));
            var reflected = ParquetSerializer.Deserialize<CitizenTwin>(new MemoryStream(bytes)).Select(ToCitizen).ToList();

            await Assert.That(generated.Count).IsEqualTo(20);
            for (int i = 0; i < expected.Count; i++)
            {
                await Assert.That(DeepComparer.Difference(expected[i], generated[i])).IsNull();
                await Assert.That(DeepComparer.Difference(reflected[i], generated[i])).IsNull();
            }
        }
    }

    [Test]
    public async Task AConstructorWithMoreParameters_AndParametersOfAnotherCase_AreUsed_AndTheOtherPropertiesAreSet()
    {
        var rows = new[] { new Mixed(5, "five") { Note = "n", Amount = 1.5m }, new Mixed(6, "six") };
        var twins = new[] { new MixedTwin(5, "five") { Note = "n", Amount = 1.5m }, new MixedTwin(6, "six") };
        byte[] bytes = Serialize(rows);

        await Assert.That(bytes.AsSpan().SequenceEqual(Serialize(twins))).IsTrue();
        var read = ParquetSerializer.Deserialize<Mixed>(new MemoryStream(bytes));
        await Assert.That(read.Select(r => (r.Id, r.Label, r.Note, r.Amount, r.Computed)).ToArray())
            .IsEquivalentTo(new (int, string, string?, decimal?, int)[] { (5, "five", "n", 1.5m, 10), (6, "six", null, null, 12) });
    }

    [Test]
    public async Task AStructWithAConstructor_IsRead()
    {
        var rows = new[] { new Pair(1, "one"), new Pair(2, null) };
        byte[] bytes = Serialize(rows);

        await Assert.That(bytes.AsSpan().SequenceEqual(Serialize(new[] { new PairTwin(1, "one"), new PairTwin(2, null) }))).IsTrue();
        var read = ParquetSerializer.Deserialize<Pair>(new MemoryStream(bytes));
        await Assert.That(read.ToArray()).IsEquivalentTo(rows);
    }

    [Test]
    public async Task ANullForAParameterThatCannotBeNull_IsRefusedByBothPathsWithTheSameMessage()
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(new[] { new NameOptional(1, null) }, stream);
        byte[] bytes = stream.ToArray();

        var generated = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<NamedRequired>(new MemoryStream(bytes)));
        var reflected = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<NamedRequiredTwin>(new MemoryStream(bytes)));

        await Assert.That(generated.Message.Replace("NamedRequired", "NamedRequiredTwin", StringComparison.Ordinal)).IsEqualTo(reflected.Message);
        await Assert.That(generated.Message).Contains("NamedRequired.Name");
    }

    private sealed record NameOptional(int Id, string? Name);

    [ParquetSerializable]
    public sealed record NamedRequired(int Id, string Name);

    public sealed record NamedRequiredTwin(int Id, string Name);
}
