using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;
using ParquetNeo.Tests.Core.Nested;

namespace ParquetNeo.Tests.Serialization;

public class NestedSerializerTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    private static async Task AssertSameNode(SchemaNode actual, SchemaNode expected, string where)
    {
        await Assert.That(actual.Name).IsEqualTo(expected.Name).Because(where);
        await Assert.That(actual.Repetition).IsEqualTo(expected.Repetition).Because(where);
        await Assert.That(actual.LogicalType).IsEqualTo(expected.LogicalType).Because(where);
        await Assert.That(actual.ConvertedType).IsEqualTo(expected.ConvertedType).Because(where);
        if (expected is PrimitiveNode primitive)
        {
            await Assert.That(actual).IsTypeOf<PrimitiveNode>().Because(where);
            await Assert.That(((PrimitiveNode)actual).Type).IsEqualTo(primitive.Type).Because(where);
            await Assert.That(((PrimitiveNode)actual).TypeLength).IsEqualTo(primitive.TypeLength).Because(where);
            return;
        }

        var group = (GroupNode)expected;
        await Assert.That(actual).IsTypeOf<GroupNode>().Because(where);
        var children = ((GroupNode)actual).Children;
        await Assert.That(children.Count).IsEqualTo(group.Children.Count).Because(where);
        for (int i = 0; i < group.Children.Count; i++)
        {
            await AssertSameNode(children[i], group.Children[i], $"{where}.{group.Children[i].Name}");
        }
    }

    [Test]
    public async Task TheSchemaOfADocument_IsTheSchemaOfTheNestedDataset()
    {
        var schema = ParquetSerializer.GetSchema<Document>();
        var expected = NestedDataset.Schema();

        await Assert.That(schema.Columns.Select(c => string.Join('.', c.Path))).IsEquivalentTo(NestedDataset.ColumnNames);
        await Assert.That(schema.Root.Children.Count).IsEqualTo(expected.Root.Children.Count);
        for (int i = 0; i < expected.Root.Children.Count; i++)
        {
            await AssertSameNode(schema.Root.Children[i], expected.Root.Children[i], expected.Root.Children[i].Name);
        }
    }

    [Test]
    [Arguments(200, 1000)]
    [Arguments(200, 64)]
    [Arguments(200, 7)]
    [Arguments(1, 1000)]
    public async Task TheLevelsAndValues_AreWhatTheReferenceShredderProduces(int rows, int rowGroupSize)
    {
        var bytes = Serialize(Document.Rows(rows), new ParquetSerializerOptions { RowGroupSize = rowGroupSize });

        using var reader = ReadHelper.Open(bytes);
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        var references = NestedDataset.Rows().Take(rows).ToList();
        await Assert.That(reader.RowGroupCount).IsEqualTo((rows + rowGroupSize - 1) / rowGroupSize);
        for (int group = 0; group < reader.RowGroupCount; group++)
        {
            var expected = ReferenceShredder.Shred(reader.Schema, references.Skip(group * rowGroupSize).Take(rowGroupSize));
            for (int column = 0; column < expected.Length; column++)
            {
                string where = $"group {group} {NestedDataset.ColumnNames[column]}";
                var (definition, repetition, values) = ReadHelper.ReadEntries(reader, group, column, 50);

                await Assert.That(definition).IsEquivalentTo(expected[column].Definition).Because(where);
                await Assert.That(repetition).IsEquivalentTo(expected[column].Repetition).Because(where);
                await Assert.That(values.Select(NestedReaderTests.Describe).SequenceEqual(expected[column].Values.Select(NestedReaderTests.Describe))).IsTrue().Because(where);
            }
        }
    }

    [Test]
    public async Task TheNestedGoldenFile_IsByteForByteWhatTheSerializerWrites()
    {
        var actual = Serialize(Document.Rows(), new ParquetSerializerOptions { RowGroupSize = 64, Writer = new ParquetWriterOptions { CreatedBy = "ParquetNeo golden" } });

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-serializer-nested.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-serializer-nested.parquet"))));
    }

    [Test]
    public async Task TheSyncAndAsyncPaths_WriteTheSameNestedBytes()
    {
        var options = new ParquetSerializerOptions { RowGroupSize = 50, Writer = new ParquetWriterOptions { CreatedBy = "same" } };
        var sync = Serialize(Document.Rows(), options);

        var asyncStream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(Document.Rows(), asyncStream, options);

        await Assert.That(Convert.ToHexString(asyncStream.ToArray())).IsEqualTo(Convert.ToHexString(sync));
    }
}
