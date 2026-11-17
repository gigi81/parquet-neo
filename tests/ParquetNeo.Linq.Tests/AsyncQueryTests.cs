using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;
using ParquetNeo.Storage;
using ParquetNeo.Tests;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Linq.Tests;

/// <summary>
/// The asynchronous run of a query: the rows are read with asynchronous calls and go through the operators as they arrive, those that
/// need every row get them in memory, and the result is what the synchronous run gives.
/// </summary>
public class AsyncQueryTests
{
    private const int Files = 4;
    private const int RowsPerFile = 400;

    /// <summary>One reader, so that the files are read one after the other in the order of their names and the rows come in the same order as when run synchronously.</summary>
    private static readonly ParquetDatasetOptions InOrder = new() { MaxDegreeOfParallelism = 1 };

    private static InMemoryStorage Setup()
    {
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 2 * 1024, TailSize = 6 * 1024 } };
        for (int file = 0; file < Files; file++)
        {
            var stream = new MemoryStream();
            ParquetSerializer.Serialize(Enumerable.Range(0, RowsPerFile).Select(i => TestDataset.Make((file * 1000) + i)).ToList(), stream, new ParquetSerializerOptions { RowGroupSize = 100 });
            storage.Put($"people/people-{file}.parquet", stream.ToArray());
        }

        return storage;
    }

    /// <summary>Runs the query both ways, and says the rows are the same, in the same order when the query is of one reader and sorted.</summary>
    private static async Task AssertSame<T>(IQueryable<T> query, bool sameOrder = true)
    {
        var expected = query.ToList();
        var actual = await query.ToListAsync();

        if (sameOrder)
        {
            await Assert.That(actual.Count).IsEqualTo(expected.Count);
            await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        }
        else
        {
            await Assert.That(actual.Select(row => row!.ToString()).Order(StringComparer.Ordinal).ToArray())
                .IsEquivalentTo(expected.Select(row => row!.ToString()).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Test]
    public async Task OperatorsThatWorkOnRowsAsTheyCome_GiveTheRowsOfTheSynchronousRun()
    {
        using var data = new TestDataset();
        var people = data.Open(InOrder);

        await AssertSame(people.Select(p => p.Id));
        await AssertSame(people.Where(p => p.Id >= 1500 && p.Age > 30).Select(p => p.Id));
        await AssertSame(people.Where(p => p.Id >= 1000).Select(p => new { p.Id, p.City }));
        await AssertSame(people.Skip(350).Take(100).Select(p => p.Id));
        await AssertSame(people.Where(p => p.Active).Skip(10).Take(30).Select(p => p.Name));
        await AssertSame(people.Take(0).Select(p => p.Id));
        await AssertSame(people.Where(p => p.Id == -1).Select(p => p.Id));
    }

    [Test]
    public async Task OperatorsThatNeedEveryRow_GiveTheRowsOfTheSynchronousRun()
    {
        using var data = new TestDataset();
        var people = data.Open(InOrder);

        await AssertSame(people.OrderBy(p => p.Age).ThenByDescending(p => p.Id).Take(50).Select(p => p.Id));
        await AssertSame(people.Where(p => p.Id < 1500).OrderBy(p => p.Age).Select(p => p.Name));
        await AssertSame(people.Select(p => p.City).Distinct());
        await AssertSame(people.GroupBy(p => p.City).Select(g => new { City = g.Key, Count = g.Count() }), sameOrder: false);
        await AssertSame(people.Where(p => p.Id < 2000).Reverse().Select(p => p.Id));
        await AssertSame(people.Where(p => p.Id < 100).Join(data.All.Where(p => p.Id < 50), outer => outer.Id, inner => inner.Id, (outer, inner) => new { outer.Id, inner.Name }));
    }

    [Test]
    public async Task StreamingOperatorsBeforeOnesThatNeedEveryRow_AreRunBeforeThem()
    {
        using var data = new TestDataset();
        var people = data.Open(InOrder);

        // Where and Select as the rows come, then the sort in memory, then Take and Select again.
        await AssertSame(people.Where(p => p.Id >= 1000).Select(p => new { p.Id, p.Age }).OrderBy(x => x.Age).ThenBy(x => x.Id).Take(25).Select(x => x.Id));
        await AssertSame(people.Where(p => p.Id >= 2000).Skip(5).OrderByDescending(p => p.Id).Skip(3).Take(10).Select(p => p.Id));
    }

    [Test]
    public async Task WithSeveralReaders_TheRowsAreTheSameButInNoOrder()
    {
        using var data = new TestDataset();
        var people = data.Open(new ParquetDatasetOptions { MaxDegreeOfParallelism = 4 });

        await AssertSame(people.Where(p => p.Age > 40).Select(p => p.Id), sameOrder: false);
        await AssertSame(people.Select(p => p.Id), sameOrder: false);
    }

    [Test]
    public async Task OneRowOperators_GiveWhatTheSynchronousOnesDo()
    {
        using var data = new TestDataset();
        var people = data.Open(InOrder);

        await Assert.That((await people.OrderBy(p => p.Id).FirstAsync()).Id).IsEqualTo(people.OrderBy(p => p.Id).First().Id);
        await Assert.That((await people.Where(p => p.Id > 1100).FirstAsync()).Id).IsEqualTo(people.Where(p => p.Id > 1100).First().Id);
        await Assert.That((await people.FirstAsync(p => p.Age == 30 && p.Id > 2000)).Id).IsEqualTo(people.First(p => p.Age == 30 && p.Id > 2000).Id);
        await Assert.That(await people.Where(p => p.Id < 0).FirstOrDefaultAsync()).IsNull();
        await Assert.That(await people.CountAsync(p => p.City == "Rome")).IsEqualTo(people.Count(p => p.City == "Rome"));
        await Assert.That(await people.Where(p => p.Age > 50).CountAsync()).IsEqualTo(people.Where(p => p.Age > 50).Count());
        await Assert.That(await people.Select(p => p.Age).Distinct().CountAsync()).IsEqualTo(people.Select(p => p.Age).Distinct().Count());
        await Assert.That(await people.AnyAsync()).IsTrue();
        await Assert.That(await people.AnyAsync(p => p.Id == 3399)).IsTrue();
        await Assert.That(await people.AnyAsync(p => p.Id == 3400)).IsFalse();
        await Assert.That(await people.Where(p => p.Id < 0).AnyAsync()).IsFalse();
        await Assert.That(async () => await people.Where(p => p.Id < 0).FirstAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExplainAsync_GivesThePlanOfExplain()
    {
        using var data = new TestDataset();
        var people = data.Open();

        await AssertSamePlan(people.Where(p => p.Id >= 3000).Select(p => p.Id));
        await AssertSamePlan(people.Where(p => p.Id >= 2000 && p.Id < 2100));
        await AssertSamePlan(people);
    }

    private static async Task AssertSamePlan<T>(IQueryable<T> query)
    {
        var expected = query.Explain();
        var actual = await query.ExplainAsync();

        await Assert.That(actual with { Columns = null }).IsEqualTo(expected with { Columns = null });
        await Assert.That(actual.Columns?.SequenceEqual(expected.Columns!) ?? expected.Columns is null).IsTrue();
    }

    [Test]
    public async Task NoCallBlocksOnTheStreamOfAFile()
    {
        var storage = Setup();
        var blocking = new NoBlockingStorage(storage);
        var people = ParquetDataset.Open<Person>(blocking, "people/", new ParquetDatasetOptions { MaxDegreeOfParallelism = 3 });

        var ids = await people.Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => p.Id).ToListAsync();
        var sorted = await people.OrderBy(p => p.Age).ThenBy(p => p.Id).Take(5).Select(p => p.Id).ToListAsync();
        var first = await people.FirstAsync(p => p.Id == 3333);
        var count = await people.Where(p => p.Id < 1000).CountAsync();
        var all = await people.CountAsync();
        var plan = await people.Where(p => p.Id >= 3000).ExplainAsync();

        await Assert.That(ids.Order().SequenceEqual(Enumerable.Range(2000, 150))).IsTrue();
        await Assert.That(sorted.Count).IsEqualTo(5);
        await Assert.That(first.Id).IsEqualTo(3333);
        await Assert.That(count).IsEqualTo(RowsPerFile);
        await Assert.That(all).IsEqualTo(Files * RowsPerFile);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(blocking.Opened).IsGreaterThan(0);
    }

    [Test]
    public async Task StoppingEarly_StopsTheReaders_WhichAreHeldBackAtABoundedNumberOfRows()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/", InOrder);

        var first = await people.Select(p => p.Id).Take(10).ToListAsync();
        var one = await people.FirstAsync();

        await Assert.That(first.Count).IsEqualTo(10);
        await Assert.That(one.Id).IsEqualTo(0);
        string[] keys;
        lock (storage.Requests)
        {
            keys = storage.Requests.Select(request => request.Key).Distinct().ToArray();
        }

        // The reader runs ahead of the one who takes the rows by up to 1024 of them, which is two files and a part of a third, not the fourth.
        await Assert.That(keys).DoesNotContain("people/people-3.parquet");
    }

    [Test]
    public async Task AFileThatCannotBeRead_ThrowsItsErrorToTheCaller_AndStopsTheOtherReaders()
    {
        var storage = Setup();
        storage.Put("people/people-2.parquet", new byte[500]);
        var people = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { MaxDegreeOfParallelism = 3 });

        await Assert.That(async () => await people.Select(p => p.Id).ToListAsync()).Throws<ParquetFormatException>();
        await Assert.That(async () => await people.CountAsync()).Throws<ParquetFormatException>();
        await Assert.That(async () => await people.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync()).Throws<ParquetFormatException>();
    }

    [Test]
    public async Task CancellingWhileTheRowsAreRead_ThrowsOperationCanceled()
    {
        using var data = new TestDataset();
        using var cancel = new CancellationTokenSource();
        int seen = 0;

        var read = async () =>
        {
            await foreach (var _ in data.Open().Select(p => p.Id).AsAsyncEnumerable(cancel.Token))
            {
                if (++seen == 5)
                {
                    await cancel.CancelAsync();
                }
            }
        };

        await Assert.That(read).Throws<OperationCanceledException>();
        await Assert.That(seen).IsLessThan(Files * RowsPerFile);
    }

    [Test]
    public async Task ACancelledToken_StopsTheOnesThatAreAwaited()
    {
        using var data = new TestDataset();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var people = data.Open();

        await Assert.That(async () => await people.CountAsync(p => p.Id > 5, cancel.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await people.FirstAsync(cancel.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await people.ExplainAsync(cancel.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task AQueryThatIsNotOfADataset_IsRunOnAnotherThread()
    {
        var rows = new[] { 3, 1, 2 }.AsQueryable();

        await Assert.That(await rows.OrderBy(i => i).ToListAsync()).IsEquivalentTo([1, 2, 3]);
        await Assert.That(await rows.FirstAsync(i => i > 1)).IsEqualTo(3);
        await Assert.That(await rows.CountAsync()).IsEqualTo(3);
        await Assert.That(async () => await rows.ExplainAsync()).Throws<ArgumentException>();
    }

    [Test]
    public async Task TheQueryIsRecordedAsMetrics_WhenRunAsynchronously()
    {
        using var metrics = new ParquetNeo.Diagnostics.ParquetNeoMetrics();
        using var recorder = new ParquetNeo.Tests.Diagnostics.MetricRecorder(metrics.Meter);
        var people = ParquetDataset.Open<Person>(Setup(), "people/", new ParquetDatasetOptions { Metrics = metrics });

        var filtered = await people.Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => p.Id).ToListAsync();
        var all = await people.Select(p => p.Id).ToListAsync();
        await people.CountAsync();

        await Assert.That(recorder.Count("parquetneo.query.duration")).IsEqualTo(3);
        await Assert.That(recorder.Count("parquetneo.query.duration", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, true))).IsEqualTo(1);
        await Assert.That(recorder.Sum("parquetneo.query.rows", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, true))).IsGreaterThanOrEqualTo(filtered.Count);
        await Assert.That(recorder.Sum("parquetneo.query.rows", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, false))).IsEqualTo(all.Count);
    }

    /// <summary>A storage whose streams fail on a synchronous read, so that a call that blocks on a file is a failure.</summary>
    private sealed class NoBlockingStorage(IParquetStorage inner) : IParquetStorage
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public async ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opened);
            return new AsyncOnlyStream(await inner.OpenReadAsync(key, cancellationToken));
        }
    }
}
