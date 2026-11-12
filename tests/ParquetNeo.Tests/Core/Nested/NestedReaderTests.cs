using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Core.Nested;

public class NestedReaderTests
{
    /// <summary>Strings are compared as their UTF-8 bytes, which is how the reader returns them.</summary>
    internal static string Describe(object value) => value switch
    {
        string text => SampleFile.Canonical(System.Text.Encoding.UTF8.GetBytes(text)),
        _ => SampleFile.Canonical(value),
    };

    [Test]
    [Arguments("pyarrow-nested-v1.parquet")]
    [Arguments("pyarrow-nested-dictionary-v1.parquet")]
    [Arguments("pyarrow-nested-v2-snappy.parquet")]
    [Arguments("pyarrow-nested-v1-smallpages.parquet")]
    public async Task ThePyArrowNestedFiles_HaveTheLevelsTheReferenceShredderProduces(string fixture)
    {
        using var reader = ReadHelper.OpenFixture(fixture);
        var expected = ReferenceShredder.Shred(reader.Schema, NestedDataset.Rows());

        await Assert.That(reader.RowCount).IsEqualTo((long)NestedDataset.RowCount);
        await Assert.That(reader.Schema.Columns.Select(c => string.Join('.', c.Path))).IsEquivalentTo(NestedDataset.ColumnNames);
        foreach (int batchSize in new[] { 1, 7, 64, 10_000 })
        {
            for (int column = 0; column < expected.Length; column++)
            {
                var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, column, batchSize);

                string where = $"{fixture} {NestedDataset.ColumnNames[column]} batch {batchSize}";
                await Assert.That(definition).IsEquivalentTo(expected[column].Definition).Because(where);
                await Assert.That(repetition).IsEquivalentTo(expected[column].Repetition).Because(where);
                await Assert.That(values.Select(Describe).SequenceEqual(expected[column].Values.Select(Describe))).IsTrue().Because(where);
            }
        }
    }

    [Test]
    public async Task TheSmallPagesFixture_ReallyHasManyPages()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-v1-smallpages.parquet")));

        // events.count has 320 entries: with 200-byte pages that is several pages for the reader to cross.
        await Assert.That(file.DataPages(0, 7).Count).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task EntriesAndRows_AreCountedSeparately()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-nested-v1.parquet");
        var group = reader.OpenRowGroup(0);

        await Assert.That(group.RowCount).IsEqualTo((long)NestedDataset.RowCount);
        // scores.list.element holds more entries than there are rows: each element, empty list and null takes one.
        using var element = group.OpenColumn(reader.Schema.FindColumn(["scores", "list", "element"])!.ColumnIndex);
        await Assert.That(element.EntryCount).IsGreaterThan(NestedDataset.RowCount);
        await Assert.That(element.EntryCount).IsEqualTo(395L);
    }

    [Test]
    public async Task TheDremelExample_ReadsBackFromAFileOurWriterMade()
    {
        var schema = DocumentExample.Schema();
        var bytes = NestedFile.Write(schema, DocumentExample.Rows());
        using var reader = ReadHelper.Open(bytes);

        var expected = DocumentExample.Expected();
        for (int c = 0; c < expected.Length; c++)
        {
            var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, c, 3);

            await Assert.That(definition).IsEquivalentTo(expected[c].Entries.Select(e => e.Definition)).Because(expected[c].Path);
            await Assert.That(repetition).IsEquivalentTo(expected[c].Entries.Select(e => e.Repetition)).Because(expected[c].Path);
            await Assert.That(values.Select(Describe)).IsEquivalentTo(expected[c].Entries.Where(e => e.Value is not null).Select(e => Describe(e.Value!))).Because(expected[c].Path);
        }
    }

    [Test]
    public async Task ARowGroupThatDeclaresTheWrongRowCount_IsReported()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-v1.parquet"));
        foreach (long wrong in new long[] { 199, 201 })
        {
            var damaged = FooterEditor.Rewrite(bytes, f => f.RowGroups[0].NumRows = wrong);
            using var reader = ReadHelper.Open(damaged);

            await Assert.That(() => ReadHelper.ReadEntries(reader, 0, 1, 50)).Throws<CorruptColumnException>().Because($"{wrong} rows");
        }
    }

    [Test]
    public async Task AColumnThatDoesNotStartWithANewRow_IsReported()
    {
        // list<int32> with required elements and one row [5]: the repetition levels are one bit-packed group "03 00" after a
        // 4-byte length. Make the first entry continue a row that never started.
        var schema = new ParquetNeo.Schema.ParquetSchema([NestedDataset.ListOf("l", FieldRepetitionType.Optional, new ParquetNeo.Schema.PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Required))]);
        var rows = new[] { new StructValue { ["l"] = NestedDataset.List(5) } };
        var bytes = NestedFile.Write(schema, rows);
        var page = TestParquetFile.Parse(bytes).DataPages(0, 0)[0];
        long body = page.Offset + page.HeaderLength;
        await Assert.That(bytes.AsSpan((int)body, 6).ToArray()).IsEquivalentTo(new byte[] { 2, 0, 0, 0, 3, 0 });
        bytes[body + 5] = 1;
        using var reader = ReadHelper.Open(bytes);

        var exception = Assert.Throws<CorruptColumnException>(() => ReadHelper.ReadEntries(reader, 0, 0, 10));

        await Assert.That(exception.Message).Contains("new row");
    }

    [Test]
    public async Task ARepetitionLevelAboveTheMaximum_IsReported()
    {
        // A list of lists has a maximum repetition level of 2, which takes 2 bits, so a 3 can be written but is out of range.
        var schema = new ParquetNeo.Schema.ParquetSchema([NestedDataset.ListOf("l", FieldRepetitionType.Optional,
            NestedDataset.ListOf("element", FieldRepetitionType.Optional, new ParquetNeo.Schema.PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Required)))]);
        var bytes = NestedFile.Write(schema, [new StructValue { ["l"] = NestedDataset.List(NestedDataset.List(1, 2)) }]);
        var page = TestParquetFile.Parse(bytes).DataPages(0, 0)[0];
        long start = page.Offset + page.HeaderLength;

        // The levels are 0, 2: one bit-packed group "03" whose packed bytes are 0b00001000 and 0.
        await Assert.That(bytes.AsSpan((int)start, 7).ToArray()).IsEquivalentTo(new byte[] { 3, 0, 0, 0, 3, 8, 0 });
        bytes[start + 5] = 0x0C; // the second level becomes 3
        using var reader = ReadHelper.Open(bytes);

        var exception = Assert.Throws<CorruptColumnException>(() => ReadHelper.ReadEntries(reader, 0, 0, 10));

        await Assert.That(exception.Message).Contains("repetition level");
    }
}
