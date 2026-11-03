using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Schema;

/// <summary>Builds the schema of files written by pyarrow (see tests/fixtures) and compares it with the original.</summary>
public class PyArrowSchemaTests
{
    [Test]
    [Arguments("pyarrow-v1.parquet")]
    [Arguments("pyarrow-v2.parquet")]
    public async Task Schema_RebuildsIntoIdenticalElements(string fixture)
    {
        var original = TestBytes.ReadMetadata(fixture).Schema;

        // pyarrow marks the root REQUIRED, but the spec says the root has no repetition type, so ToElements
        // omits it. That is the only intended difference.
        await Assert.That(original[0].RepetitionType).IsEqualTo(FieldRepetitionType.Required);
        original[0].RepetitionType = null;

        var rebuilt = ParquetSchema.FromElements(original).ToElements();

        await Assert.That(rebuilt.Select(TestBytes.ToHex)).IsEquivalentTo(original.Select(TestBytes.ToHex));
    }

    [Test]
    public async Task Columns_MatchTheColumnChunksOfTheFile()
    {
        var metadata = TestBytes.ReadMetadata("pyarrow-v1.parquet");
        var schema = ParquetSchema.FromElements(metadata.Schema);

        await Assert.That(schema.Columns.Count).IsEqualTo(metadata.RowGroups[0].Columns.Count);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var chunk = metadata.RowGroups[0].Columns[i].MetaData!;
            await Assert.That(schema.Columns[i].Path).IsEquivalentTo(chunk.PathInSchema);
            await Assert.That(schema.Columns[i].Type).IsEqualTo(chunk.Type);
        }
    }

    [Test]
    public async Task Levels_MatchWhatPyArrowReports()
    {
        var schema = ParquetSchema.FromElements(TestBytes.ReadMetadata("pyarrow-v1.parquet").Schema);

        (string Path, int Definition, int Repetition)[] expected =
        [
            ("id", 1, 0),
            ("name", 1, 0),
            ("tags.list.element", 3, 1),
            ("point.x", 2, 0),
            ("point.y", 2, 0),
            ("attrs.key_value.key", 2, 1),
            ("attrs.key_value.value", 3, 1),
        ];

        foreach (var (path, definition, repetition) in expected)
        {
            var column = schema.FindColumn(path.Split('.'));
            await Assert.That(column).IsNotNull();
            await Assert.That(column!.MaxDefinitionLevel).IsEqualTo(definition);
            await Assert.That(column.MaxRepetitionLevel).IsEqualTo(repetition);
        }
    }

    [Test]
    public async Task LogicalTypes_AreResolvedOnColumns()
    {
        var schema = ParquetSchema.FromElements(TestBytes.ReadMetadata("pyarrow-v1.parquet").Schema);

        await Assert.That(schema.FindColumn(["name"])!.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(schema.FindColumn(["amount"])!.LogicalType).IsEqualTo(LogicalType.Decimal(2, 10));
        await Assert.That(schema.FindColumn(["half"])!.LogicalType).IsEqualTo(LogicalType.Float16);
        await Assert.That(schema.FindColumn(["half"])!.TypeLength).IsEqualTo(2);
        await Assert.That(schema.FindColumn(["fixed"])!.LogicalType).IsNull();
        await Assert.That(schema.Root.FindChild("tags")!.LogicalType).IsEqualTo(LogicalType.List);
        await Assert.That(schema.Root.FindChild("attrs")!.LogicalType).IsEqualTo(LogicalType.Map);
    }
}
