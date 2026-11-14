using ParquetNeo.Data;
using ParquetNeo.Filtering;

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
}
