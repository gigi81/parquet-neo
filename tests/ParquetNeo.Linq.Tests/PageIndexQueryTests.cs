namespace ParquetNeo.Linq.Tests;

/// <summary>Queries over files with a page index give the same rows as over files without one, from the pages that may hold them.</summary>
public class PageIndexQueryTests
{
    private static ParquetWriterOptions Indexed => new() { WritePageIndex = true, DataPageSize = 500 };

    [Test]
    public async Task ARangeOfIds_GivesTheSameRows_WithAndWithoutThePageIndex()
    {
        using var with = new TestDataset(Indexed);
        using var without = new TestDataset();

        var queries = new Func<ParquetDataset<Person>, List<int>>[]
        {
            data => data.Where(p => p.Id >= 1030 && p.Id < 1070).Select(p => p.Id).OrderBy(i => i).ToList(),
            data => data.Where(p => p.Id == 2222).Select(p => p.Id).ToList(),
            data => data.Where(p => p.Id < 20 || p.Id >= 3390).Select(p => p.Id).OrderBy(i => i).ToList(),
            data => data.Where(p => p.Age == 30 && p.Id > 2100 && p.Id <= 2250).Select(p => p.Id).OrderBy(i => i).ToList(),
            data => data.Where(p => p.Name == null && p.Id >= 3000).Select(p => p.Id).OrderBy(i => i).ToList(),
            data => data.Where(p => !(p.Id >= 50 && p.Id < 350)).Select(p => p.Id).OrderBy(i => i).Take(500).ToList(),
        };

        foreach (var query in queries)
        {
            var expected = query(without.Open());
            await Assert.That(query(with.Open())).IsEquivalentTo(expected);
            await Assert.That(expected.Count).IsGreaterThan(0);
        }

        // And the rows are those of the people, whole.
        var rows = with.Open().Where(p => p.Id >= 2300 && p.Id < 2310).OrderBy(p => p.Id).ToList();
        await Assert.That(rows.Select(r => r.Id)).IsEquivalentTo(Enumerable.Range(2300, 10));
        await Assert.That(rows.All(r => r.Home.Street == TestDataset.Make(r.Id).Home.Street && r.Tags.Count == TestDataset.Make(r.Id).Tags.Count)).IsTrue();
    }
}
