using ParquetNeo.Data;

namespace ParquetNeo.Tests.Filtering;

/// <summary>The ADO.NET reader skips the row groups a filter rules out.</summary>
public class FilterDataReaderTests
{
    /// <summary>Five row groups of ten rows: the ids 0 to 9, 10 to 19, and so on.</summary>
    private static byte[] Ids() => FilterFile.CreateBytes([FilterFile.Column<long>("Id"), FilterFile.Column<int>("Group")], 5, (group, writer) =>
    {
        writer.NextColumn().WriteBatch(Enumerable.Range(group * 10, 10).Select(i => (long)i).ToArray());
        writer.NextColumn().WriteBatch(Enumerable.Repeat(group, 10).ToArray());
    });

    private static List<long> ReadIds(ParquetDataReaderOptions? options)
    {
        using var reader = new ParquetDataReader(new MemoryStream(Ids()), options);
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [Test]
    public async Task AllTheRowsAreRead_WithoutAFilter()
    {
        await Assert.That(ReadIds(null).Count).IsEqualTo(50);
    }

    [Test]
    public async Task TheRowGroupsTheFilterRulesOut_AreNotRead_AndTheRowsOfTheOthersAllAre()
    {
        var ids = ReadIds(new ParquetDataReaderOptions { RowGroupFilter = ParquetFilter.Column("Id").GreaterThanOrEqual(25).And(ParquetFilter.Column("Id").LessThan(32)) });

        // The row groups of 20 to 29 and of 30 to 39: whole, though only some of their rows satisfy the filter.
        await Assert.That(ids.Count).IsEqualTo(20);
        await Assert.That(ids.Min()).IsEqualTo(20);
        await Assert.That(ids.Max()).IsEqualTo(39);
    }

    [Test]
    public async Task AFilterThatRulesOutEveryRowGroup_GivesNoRows()
    {
        var ids = ReadIds(new ParquetDataReaderOptions { RowGroupFilter = ParquetFilter.Column("Id").GreaterThan(1000) });

        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task TheFilterMayNameAColumnThatIsNotRead()
    {
        var options = new ParquetDataReaderOptions { Columns = ["Id"], RowGroupFilter = ParquetFilter.Column("Group").Equal(3) };

        var ids = ReadIds(options);

        await Assert.That(ids.Select(id => id / 10).Distinct().ToArray()).IsEquivalentTo(new long[] { 3 });
    }

    [Test]
    public async Task ThePagesTheFilterRulesOut_AreNotRead_WhenTheFileHasAPageIndex()
    {
        var bytes = FilterFile.CreateBytes(
            [FilterFile.Column<long>("Id"), FilterFile.Column<int>("Group")],
            1,
            (_, writer) =>
            {
                writer.NextColumn().WriteBatch(Enumerable.Range(0, 4000).Select(i => (long)i).ToArray());
                writer.NextColumn().WriteBatch(Enumerable.Range(0, 4000).Select(i => i * 2).ToArray());
            },
            new ParquetWriterOptions { WritePageIndex = true, DataPageSize = 500 });
        using var reader = new ParquetDataReader(new MemoryStream(bytes), new ParquetDataReaderOptions { RowGroupFilter = ParquetFilter.Column("Id").Equal(1234L) });
        var rows = new List<(long Id, int Group)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        await Assert.That(rows.Count).IsBetween(1, 400);
        await Assert.That(rows).Contains((1234L, 2468));
        await Assert.That(rows.All(r => r.Group == r.Id * 2)).IsTrue();
    }
}
