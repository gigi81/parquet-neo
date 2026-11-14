using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

public class NestedValidationTests
{
    private static ParquetSchema ListSchema() =>
        new([NestedDataset.ListOf("l", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional))]);

    private static (ColumnWriter Column, RowGroupWriter Group, ParquetFileWriter File) Open(ParquetSchema? schema = null)
    {
        var file = new ParquetFileWriter(new MemoryStream(), schema ?? ListSchema(), leaveOpen: true);
        var group = file.AppendRowGroup();
        return (group.NextColumn(), group, file);
    }

    // ---- writing ----

    [Test]
    public async Task ARepeatedColumn_NeedsRepetitionLevels()
    {
        var (column, _, _) = Open();

        await Assert.That(() => column.WriteBatch([1, 2], [3, 3])).Throws<ArgumentException>();
    }

    [Test]
    public async Task RepetitionLevels_MustMatchTheDefinitionLevels_InNumber()
    {
        var (column, _, _) = Open();

        await Assert.That(() => column.WriteBatch([1, 2], [3, 3], [0])).Throws<ArgumentException>();
        await Assert.That(() => column.WriteBatch([1, 2], [3, 3], [0, 1, 1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task RepetitionLevels_AboveTheMaximum_AreRejected()
    {
        var (column, _, _) = Open();

        await Assert.That(() => column.WriteBatch([1, 2], [3, 3], [0, 2])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => column.WriteBatch([1, 2], [3, 3], [0, -1])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TheFirstEntry_MustStartARow()
    {
        var (column, _, _) = Open();

        var exception = Assert.Throws<ArgumentException>(() => column.WriteBatch([1], [3], [1]));

        await Assert.That(exception.Message).Contains("start a row");
    }

    [Test]
    public async Task ARowMayContinueInTheNextBatch_ButNotBeforeTheFirstEntry()
    {
        var (column, group, file) = Open();

        column.WriteBatch([1, 2], [3, 3], [0, 1]);
        column.WriteBatch([3, 4], [3, 3], [1, 1]); // the same row goes on
        column.WriteBatch([5], [3], [0]);
        group.Close();
        file.Close();

        await Assert.That(column.RowCount).IsEqualTo(2L);
        await Assert.That(column.EntryCount).IsEqualTo(5L);
    }

    [Test]
    public async Task ANonRepeatedColumn_TakesNoRepetitionLevels()
    {
        var schema = new ParquetSchema([new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Optional)]);
        var (column, _, _) = Open(schema);

        await Assert.That(() => column.WriteBatch([1], [1], [0])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ARepeatedColumnWithoutValues_CanBeAllEmptyLists()
    {
        var (column, group, file) = Open();

        column.WriteBatch(ReadOnlySpan<int>.Empty, [1, 1, 0], [0, 0, 0]); // an empty list, another one and a null list

        group.Close();
        file.Close();
        await Assert.That(column.RowCount).IsEqualTo(3L);
    }

    [Test]
    public async Task ColumnsOfARowGroup_MustHaveTheSameNumberOfRows_NotEntries()
    {
        var schema = new ParquetSchema(
        [
            NestedDataset.ListOf("a", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional)),
            NestedDataset.ListOf("b", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional)),
        ]);
        var file = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = file.AppendRowGroup();

        group.NextColumn().WriteBatch([1, 2, 3], [3, 3, 3], [0, 1, 1]); // 1 row, 3 entries
        group.NextColumn().WriteBatch([1, 2, 3], [3, 3, 3], [0, 0, 0]); // 3 rows, 3 entries

        await Assert.That(() => group.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ColumnsWithTheSameRows_MayHaveDifferentEntries()
    {
        var schema = new ParquetSchema(
        [
            NestedDataset.ListOf("a", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional)),
            NestedDataset.ListOf("b", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional)),
        ]);
        var stream = new MemoryStream();
        using (var file = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = file.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2, 3], [3, 3, 3], [0, 1, 1]);
            group.NextColumn().WriteBatch([7, 8], [3, 3], [0, 1]);
        }

        var parsed = TestParquetFile.Parse(stream.ToArray());
        parsed.AssertStructure();

        await Assert.That(parsed.Metadata.RowGroups[0].NumRows).IsEqualTo(1L);
        await Assert.That(parsed.Chunk(0, 0).NumValues).IsEqualTo(3L);
        await Assert.That(parsed.Chunk(0, 1).NumValues).IsEqualTo(2L);
    }

    // ---- reading ----

    [Test]
    public async Task ReadingARepeatedColumn_NeedsARepetitionLevelSpan_OfTheSameLength()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), NestedDataset.Rows().ToArray());
        using var reader = ReadHelper.Open(bytes);
        using var column = reader.OpenRowGroup(0).OpenColumn(1);
        var values = new int[10];

        await Assert.That(() => column.ReadBatch(values, new int[10])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadBatch(values, new int[10], new int[9])).Throws<ArgumentException>();
        await Assert.That(() => column.ReadBatch(values, new int[10], new int[10])).ThrowsNothing();
    }

    [Test]
    public async Task ReadingANonRepeatedColumn_TakesNoRepetitionLevels()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), NestedDataset.Rows().ToArray());
        using var reader = ReadHelper.Open(bytes);
        using var column = reader.OpenRowGroup(0).OpenColumn(0); // id: required, not repeated

        await Assert.That(() => column.ReadBatch(new long[10], default, new int[10])).Throws<ArgumentException>();
    }

    [Test]
    public async Task TheValuesSpan_NeedsRoomForEveryRequestedEntry()
    {
        var bytes = NestedFile.Write(NestedDataset.Schema(), NestedDataset.Rows().ToArray());
        using var reader = ReadHelper.Open(bytes);
        using var column = reader.OpenRowGroup(0).OpenColumn(1);

        await Assert.That(() => column.ReadBatch(new int[5], new int[10], new int[10])).Throws<ArgumentException>();
    }
}
