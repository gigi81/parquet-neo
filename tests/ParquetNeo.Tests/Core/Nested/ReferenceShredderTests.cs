namespace ParquetNeo.Tests.Core.Nested;

public class ReferenceShredderTests
{
    [Test]
    public async Task TheDremelPaperExample_ShredsToThePublishedLevels()
    {
        var schema = DocumentExample.Schema();

        var shredded = ReferenceShredder.Shred(schema, DocumentExample.Rows());

        var expected = DocumentExample.Expected();
        await Assert.That(shredded.Length).IsEqualTo(expected.Length);
        for (int c = 0; c < expected.Length; c++)
        {
            var (path, entries) = expected[c];
            await Assert.That(string.Join('.', shredded[c].Column.Path)).IsEqualTo(path);
            await Assert.That(shredded[c].Definition).IsEquivalentTo(entries.Select(e => e.Definition)).Because(path);
            await Assert.That(shredded[c].Repetition).IsEquivalentTo(entries.Select(e => e.Repetition)).Because(path);
            await Assert.That(shredded[c].Values.Select(v => v.ToString())).IsEquivalentTo(entries.Where(e => e.Value is not null).Select(e => e.Value!.ToString())).Because(path);
        }
    }

    [Test]
    public async Task TheMaximumLevels_OfTheExample_AreThoseThePaperGives()
    {
        var columns = DocumentExample.Schema().Columns;

        await Assert.That(columns.Select(c => (c.MaxDefinitionLevel, c.MaxRepetitionLevel))).IsEquivalentTo(
            new[] { (0, 0), (2, 1), (2, 1), (2, 2), (3, 2), (2, 1) });
    }

    [Test]
    public async Task TheNestedDataset_HasTheShapeItsFormulasPromise()
    {
        var shredded = ReferenceShredder.Shred(NestedDataset.Schema(), NestedDataset.Rows());

        // Every leaf has at least one entry for each row, and its first entry of a row has repetition level 0.
        await Assert.That(shredded.All(c => c.RowCount == NestedDataset.RowCount)).IsTrue();
        await Assert.That(shredded.Select(c => string.Join('.', c.Column.Path))).IsEquivalentTo(NestedDataset.ColumnNames);
        await Assert.That(shredded[0].Values.Count).IsEqualTo(NestedDataset.RowCount); // the required id column
    }
}
