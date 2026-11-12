using ParquetNeo.Core;
using ParquetNeo.Nested;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

public class ColumnStructureTests
{
    private static string Describe(ColumnStructure structure) =>
        string.Join(" | ", structure.Levels.Select(level => level.Kind == StructureLevelKind.Optional
            ? $"{level.Node.Name}:valid[{string.Join(',', level.Validity!.Select(v => v ? 1 : 0))}]"
            : $"{level.Node.Name}:offsets[{string.Join(',', level.Offsets!)}]"));

    // ---- the Dremel example, worked out by hand ----

    private static ColumnStructure DocumentColumn(string path)
    {
        var schema = DocumentExample.Schema();
        var column = schema.Columns.Single(c => string.Join('.', c.Path) == path);
        var shredded = ReferenceShredder.Shred(schema, DocumentExample.Rows()).Single(s => s.Column == column);
        return ColumnStructure.FromLevels(column, shredded.Definition.ToArray(), column.MaxRepetitionLevel > 0 ? shredded.Repetition.ToArray() : default);
    }

    [Test]
    public async Task TheDocumentExample_HasTheStructureWorkedOutByHand()
    {
        // Row 1 has 3 names, row 2 has 1. The names have 2, 0, 1 and 0 languages.
        await Assert.That(Describe(DocumentColumn("Name.Language.Code"))).IsEqualTo("Name:offsets[0,3,4] | Language:offsets[0,2,2,3,3]");
        // Links is present in both rows; row 1 has no backward links, row 2 has two.
        await Assert.That(Describe(DocumentColumn("Links.Backward"))).IsEqualTo("Links:valid[1,1] | Backward:offsets[0,0,2]");
        await Assert.That(Describe(DocumentColumn("Links.Forward"))).IsEqualTo("Links:valid[1,1] | Forward:offsets[0,3,4]");
        // The third name has no URL.
        await Assert.That(Describe(DocumentColumn("Name.Url"))).IsEqualTo("Name:offsets[0,3,4] | Url:valid[1,1,0,1]");
        // Country is optional inside the languages: us, none, then the absent language of name 2 and of name 4 are not items.
        await Assert.That(Describe(DocumentColumn("Name.Language.Country"))).IsEqualTo("Name:offsets[0,3,4] | Language:offsets[0,2,2,3,3] | Country:valid[1,0,1]");
    }

    [Test]
    public async Task TheDocumentExample_GivesItsLevelsBack()
    {
        var schema = DocumentExample.Schema();
        var shredded = ReferenceShredder.Shred(schema, DocumentExample.Rows());
        foreach (var column in shredded.Where(s => s.Column.MaxDefinitionLevel > 0))
        {
            var structure = ColumnStructure.FromLevels(column.Column, column.Definition.ToArray(), column.Column.MaxRepetitionLevel > 0 ? column.Repetition.ToArray() : default);

            structure.GetLevels(out int[] definition, out int[] repetition);

            string name = string.Join('.', column.Column.Path);
            await Assert.That(definition).IsEquivalentTo(column.Definition).Because(name);
            await Assert.That(repetition).IsEquivalentTo(column.Column.MaxRepetitionLevel > 0 ? column.Repetition : []).Because(name);
        }
    }

    // ---- two independent references agree ----

    private static async Task AssertConversions(ParquetSchema schema, IReadOnlyList<StructValue> rows, string where)
    {
        foreach (var shredded in ReferenceShredder.Shred(schema, rows))
        {
            var column = shredded.Column;
            if (column.MaxDefinitionLevel == 0)
            {
                continue; // required all the way up: there is no structure
            }

            var expected = ReferenceStructure.Build(column, rows);
            var definition = shredded.Definition.ToArray();
            var repetition = column.MaxRepetitionLevel > 0 ? shredded.Repetition.ToArray() : [];

            var fromLevels = ColumnStructure.FromLevels(column, definition, repetition);
            fromLevels.GetLevels(out int[] backDefinition, out int[] backRepetition);
            expected.GetLevels(out int[] expectedDefinition, out int[] expectedRepetition);

            string name = $"{where} {string.Join('.', column.Path)}";
            await Assert.That(Describe(fromLevels)).IsEqualTo(Describe(expected)).Because(name);
            await Assert.That(fromLevels.RowCount).IsEqualTo(rows.Count).Because(name);
            await Assert.That(backDefinition).IsEquivalentTo(definition).Because(name);
            await Assert.That(backRepetition).IsEquivalentTo(repetition).Because(name);
            await Assert.That(expectedDefinition).IsEquivalentTo(definition).Because(name);
            await Assert.That(expectedRepetition).IsEquivalentTo(repetition).Because(name);
        }
    }

    [Test]
    public async Task TheNestedDataset_ConvertsBothWays()
    {
        await AssertConversions(NestedDataset.Schema(), NestedDataset.Rows().ToArray(), "dataset");
    }

    [Test]
    public async Task TheDocumentExample_ConvertsBothWays()
    {
        await AssertConversions(DocumentExample.Schema(), DocumentExample.Rows(), "document");
    }

    [Test]
    public async Task RandomSchemasAndRows_ConvertBothWays_AndSurviveAFileRoundTrip()
    {
        for (int seed = 0; seed < 250; seed++)
        {
            var random = new Random(seed);
            var schema = RandomNested.Schema(random);
            var rows = RandomNested.Rows(schema, random, 12);
            await AssertConversions(schema, rows, $"seed {seed}");

            // The same data through the writer and the reader.
            var fresh = new ParquetSchema(schema.Root.Children.Select(Clone).ToList());
            var bytes = NestedFile.Write(fresh, rows, new ParquetWriterOptions { DataPageSize = 64 }, batchEntries: 5);
            TestParquetFile.Parse(bytes).AssertStructure();
            using var reader = ReadHelper.Open(bytes);
            var expected = ReferenceShredder.Shred(reader.Schema, rows);
            for (int c = 0; c < expected.Length; c++)
            {
                var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, c, 7);
                string name = $"seed {seed} column {c}";
                await Assert.That(definition).IsEquivalentTo(expected[c].Definition).Because(name);
                await Assert.That(repetition).IsEquivalentTo(expected[c].Repetition).Because(name);
                await Assert.That(values.Cast<int>()).IsEquivalentTo(expected[c].Values.Cast<int>()).Because(name);
            }
        }
    }

    /// <summary>A copy of a schema subtree, as nodes can belong to one schema only.</summary>
    private static SchemaNode Clone(SchemaNode node) => node switch
    {
        GroupNode group => new GroupNode(group.Name, group.Repetition, group.Children.Select(Clone).ToList()),
        PrimitiveNode leaf => new PrimitiveNode(leaf.Name, leaf.Type, leaf.Repetition),
        _ => throw new NotSupportedException(),
    };

    // ---- files from another writer ----

    [Test]
    [Arguments("pyarrow-nested-v1.parquet")]
    [Arguments("pyarrow-nested-v2-snappy.parquet")]
    public async Task TheLevelsOfPyArrowFiles_ConvertToTheStructureOfTheRows(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);
        var rows = NestedDataset.Rows().ToArray();

        for (int c = 1; c < reader.Schema.Columns.Count; c++)
        {
            var column = reader.Schema.Columns[c];
            var (definition, repetition, _) = ReadHelper.ReadEntries(reader, 0, c, 33);

            var structure = ColumnStructure.FromLevels(column, definition, column.MaxRepetitionLevel > 0 ? repetition : default);

            string name = string.Join('.', column.Path);
            await Assert.That(Describe(structure)).IsEqualTo(Describe(ReferenceStructure.Build(column, rows))).Because(name);
        }
    }

    // ---- writing from a structure ----

    [Test]
    public async Task AFile_CanBeWrittenFromStructuresInsteadOfLevels()
    {
        var schema = NestedDataset.Schema();
        var rows = NestedDataset.Rows().ToArray();
        var shredded = ReferenceShredder.Shred(schema, rows);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            foreach (var column in shredded)
            {
                var columnWriter = group.NextColumn();
                if (column.Column.MaxDefinitionLevel == 0)
                {
                    NestedFile.WriteColumn(columnWriter, column); // required all the way up: plain values
                    continue;
                }

                var structure = ReferenceStructure.Build(column.Column, rows);
                switch (column.Column.Type)
                {
                    case PhysicalType.Int32: columnWriter.WriteBatch(column.Values.Cast<int>().ToArray(), structure); break;
                    case PhysicalType.ByteArray: columnWriter.WriteStrings(column.Values.Cast<string>().ToArray(), structure); break;
                    default: throw new NotSupportedException(column.Column.Type.ToString());
                }
            }
        }

        var bytes = stream.ToArray();
        var viaLevels = NestedFile.Write(NestedDataset.Schema(), rows);

        await Assert.That(bytes.AsSpan().SequenceEqual(viaLevels)).IsTrue(); // the same file, byte for byte
    }

    [Test]
    public async Task WritingAStructure_OfAnotherColumn_IsRejected()
    {
        var schema = NestedDataset.Schema();
        var rows = NestedDataset.Rows().ToArray();
        var structure = ReferenceStructure.Build(schema.Columns[1], rows);
        var file = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = file.AppendRowGroup();
        group.NextColumn().WriteBatch([1L, 2L], default(ReadOnlySpan<int>), default); // the id column

        var other = group.NextColumn();

        await Assert.That(() => other.WriteBatch(new int[3], ReferenceStructure.Build(schema.Columns[2], rows))).Throws<ArgumentException>();
        await Assert.That(() => other.WriteBatch(new int[3], null!)).Throws<ArgumentNullException>();
        _ = structure;
    }

    // ---- validation ----

    [Test]
    public async Task Create_ChecksTheLevelsAgainstTheColumn()
    {
        var schema = NestedDataset.Schema();
        var column = schema.Columns[1]; // scores.list.element: scores (optional), list (repeated), element (optional)
        var nodes = new List<SchemaNode>();
        for (SchemaNode? node = column; node?.Parent is not null; node = node.Parent)
        {
            nodes.Insert(0, node);
        }

        var scores = StructureLevel.ForOptional(nodes[0], [true, false]);
        var list = StructureLevel.ForRepeated(nodes[1], [0, 2, 2]);
        var element = StructureLevel.ForOptional(nodes[2], [true, false]);

        await Assert.That(() => ColumnStructure.Create(column, [scores, list, element])).ThrowsNothing();
        await Assert.That(() => ColumnStructure.Create(column, [scores, list])).Throws<ArgumentException>(); // a level missing
        await Assert.That(() => ColumnStructure.Create(column, [list, scores, element])).Throws<ArgumentException>(); // wrong order
        await Assert.That(() => ColumnStructure.Create(column, [scores, StructureLevel.ForRepeated(nodes[1], [0, 2, 2, 4]), element])).Throws<ArgumentException>(); // 3 lists for 2 rows
        await Assert.That(() => ColumnStructure.Create(column, [scores, list, StructureLevel.ForOptional(nodes[2], [true])])).Throws<ArgumentException>(); // 1 element validity for 2 elements
        await Assert.That(() => ColumnStructure.Create(schema.Columns[0], [scores])).Throws<ArgumentException>(); // the id column has no structure
    }

    [Test]
    public async Task Levels_CheckTheirOwnArrays()
    {
        var schema = NestedDataset.Schema();
        var optional = schema.Columns[1].Parent!.Parent!; // the list group
        var repeated = schema.Columns[1].Parent!;
        var required = schema.Columns[0];

        await Assert.That(() => StructureLevel.ForRepeated(repeated, [])).Throws<ArgumentException>();
        await Assert.That(() => StructureLevel.ForRepeated(repeated, [1, 2])).Throws<ArgumentException>();
        await Assert.That(() => StructureLevel.ForRepeated(repeated, [0, 3, 2])).Throws<ArgumentException>();
        await Assert.That(() => StructureLevel.ForRepeated(optional, [0, 1])).Throws<ArgumentException>();
        await Assert.That(() => StructureLevel.ForOptional(repeated, [true])).Throws<ArgumentException>();
        await Assert.That(() => StructureLevel.ForOptional(required, [true])).Throws<ArgumentException>();
    }

    [Test]
    public async Task FromLevels_RejectsLevelsThatCannotBeRight()
    {
        var column = NestedDataset.Schema().Columns[1]; // max definition level 3, max repetition level 1

        await Assert.That(() => ColumnStructure.FromLevels(column, [3, 3], [0])).Throws<ArgumentException>(); // lengths differ
        await Assert.That(() => ColumnStructure.FromLevels(column, [3, 3], default)).Throws<ArgumentException>(); // repetition levels missing
        await Assert.That(() => ColumnStructure.FromLevels(column, [4], [0])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ColumnStructure.FromLevels(column, [3], [2])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ColumnStructure.FromLevels(column, [3, 3], [1, 1])).Throws<ArgumentException>(); // does not start a row
        // matrix.list.element.list.element: an entry that repeats inside an inner list that no earlier entry started
        await Assert.That(() => ColumnStructure.FromLevels(NestedDataset.Schema().Columns[2], [1, 5], [0, 2])).Throws<ArgumentException>();
        await Assert.That(() => ColumnStructure.FromLevels(NestedDataset.Schema().Columns[0], [0])).Throws<ArgumentException>();
        await Assert.That(ColumnStructure.FromLevels(column, default, default).RowCount).IsEqualTo(0);
    }

    [Test]
    public async Task AnInconsistentStructure_IsReportedWhenLevelsAreTakenFromIt()
    {
        // The list is null for the only row, yet the structure claims it holds two elements.
        var column = NestedDataset.Schema().Columns[1];
        var nodes = new List<SchemaNode>();
        for (SchemaNode? node = column; node?.Parent is not null; node = node.Parent)
        {
            nodes.Insert(0, node);
        }

        var structure = ColumnStructure.Create(column,
        [
            StructureLevel.ForOptional(nodes[0], [false]),
            StructureLevel.ForRepeated(nodes[1], [0, 2]),
            StructureLevel.ForOptional(nodes[2], [true, true]),
        ]);

        await Assert.That(() => structure.GetLevels(out _, out _)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(new int[] { }, 0)]
    [Arguments(new[] { 0 }, 0)]
    [Arguments(new[] { 0, 1, 1 }, 0)]
    [Arguments(new[] { 0, 1, 0 }, 2)]
    [Arguments(new[] { 0, 1, 0, 1, 1, 0, 2 }, 5)]
    [Arguments(new[] { 1, 1 }, 0)]
    public async Task CountEntriesInCompleteRows_StopsBeforeTheLastRowStart(int[] repetition, int expected)
    {
        await Assert.That(ColumnStructure.CountEntriesInCompleteRows(repetition)).IsEqualTo(expected);
    }
}
