namespace ParquetNeo.Linq.Tests;

/// <summary>The asynchronous methods give what the synchronous ones do, and stop when asked.</summary>
public class AsyncTests
{
    [Test]
    public async Task ToListAsyncAndToArrayAsync_GiveTheRows()
    {
        using var data = new TestDataset();
        var query = data.Open().Where(p => p.Id >= 3000).Select(p => p.Id);

        var list = await query.ToListAsync();
        var array = await query.ToArrayAsync();

        await Assert.That(list.Count).IsEqualTo(400);
        await Assert.That(array.Order().SequenceEqual(list.Order())).IsTrue();
    }

    [Test]
    public async Task TheRowsCanBeAwaitedOneByOne()
    {
        using var data = new TestDataset();
        var ids = new List<int>();

        await foreach (var id in data.Open().Where(p => p.Id < 100).Select(p => p.Id).AsAsyncEnumerable())
        {
            ids.Add(id);
        }

        await Assert.That(ids.Order().SequenceEqual(Enumerable.Range(0, 100))).IsTrue();
    }

    [Test]
    public async Task FirstCountAndAny_AreAwaited()
    {
        using var data = new TestDataset();
        var dataset = data.Open();

        await Assert.That((await dataset.FirstAsync(p => p.Id == 2001)).Id).IsEqualTo(2001);
        await Assert.That(await dataset.FirstOrDefaultAsync(p => p.Id == 123_456)).IsNull();
        await Assert.That(await dataset.CountAsync()).IsEqualTo(1600);
        await Assert.That(await dataset.AnyAsync(p => p.Id == 3399)).IsTrue();
    }

    [Test]
    public async Task StoppingEarly_EndsTheReading()
    {
        using var data = new TestDataset();
        int seen = 0;

        await foreach (var _ in data.Open().AsAsyncEnumerable())
        {
            if (++seen == 10)
            {
                break;
            }
        }

        await Assert.That(seen).IsEqualTo(10);
    }

    [Test]
    public async Task ACancelledToken_StopsTheEnumeration()
    {
        using var data = new TestDataset();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.That(async () => await data.Open().ToListAsync(cancel.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task AnErrorOfTheQuery_ReachesTheCaller()
    {
        var dataset = ParquetDataset.Open<Person>(Path.Combine(Path.GetTempPath(), "nothing-" + Guid.NewGuid().ToString("N")));

        await Assert.That(async () => await dataset.ToListAsync()).Throws<FileNotFoundException>();
    }
}
