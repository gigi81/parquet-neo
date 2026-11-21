using ParquetNeo.Serialization;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Linq.Tests;

/// <summary>Datasets over the files of a storage (an object storage, in the real world): the rows, what is skipped, and how little of the files is read.</summary>
public class StorageDatasetTests
{
    private const int Files = 4;
    private const int RowsPerFile = 400;

    /// <summary>Four files of 400 people in row groups of 100, under <c>people/</c>, ids in order as in <see cref="TestDataset"/>; and some other objects.</summary>
    private static InMemoryStorage Setup(RangeReadStreamOptions? options = null)
    {
        var storage = new InMemoryStorage { Options = options ?? new RangeReadStreamOptions { BlockSize = 2 * 1024, TailSize = 6 * 1024 } };
        for (int file = 0; file < Files; file++)
        {
            var stream = new MemoryStream();
            ParquetSerializer.Serialize(Enumerable.Range(0, RowsPerFile).Select(i => TestDataset.Make((file * 1000) + i)).ToList(), stream, new ParquetSerializerOptions { RowGroupSize = 100 });
            storage.Put($"people/people-{file}.parquet", stream.ToArray());
        }

        storage.Put("people/readme.txt", [1, 2, 3]);
        storage.Put("other/elsewhere.parquet", [4, 5, 6]);
        return storage;
    }

    private static long FilesSize(InMemoryStorage storage) => storage.ListAsync("people/").ToBlockingEnumerable().Where(o => o.Key.EndsWith(".parquet", StringComparison.Ordinal)).Sum(o => o.Length);

    [Test]
    public async Task TheRowsOfTheFilesBelowThePrefix_AreTheRows()
    {
        var storage = Setup();

        var ids = ParquetDataset.Open<Person>(storage, "people/").Select(p => p.Id).ToList();

        await Assert.That(ids.Count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(ids.Order().ToArray()).IsEquivalentTo(Enumerable.Range(0, Files).SelectMany(f => Enumerable.Range(f * 1000, RowsPerFile)).ToArray());
    }

    [Test]
    public async Task AWhere_GivesTheRowsOfTheMemory_AndReadsLittleOfTheFiles()
    {
        var storage = Setup();
        using var local = new TestDataset();

        var rows = ParquetDataset.Open<Person>(storage, "people/").Where(p => p.Id >= 2000 && p.Id < 2150 && p.Name != null).OrderBy(p => p.Id).ToList();
        var expected = local.All.Where(p => p.Id >= 2000 && p.Id < 2150 && p.Name != null).OrderBy(p => p.Id).ToList();

        await Assert.That(rows.Select(p => p.Id)).IsEquivalentTo(expected.Select(p => p.Id));
        await Assert.That(rows.Select(p => p.Name)).IsEquivalentTo(expected.Select(p => p.Name));

        // The footers of the 4 files, and the first two row groups of one: a part of the files.
        await Assert.That(storage.BytesRead).IsLessThan(FilesSize(storage) / 2);
    }

    [Test]
    public async Task AQueryThatSelectsSomeColumns_ReadsLessThanOneThatSelectsAll()
    {
        var narrow = Setup();
        var wide = Setup();

        var ids = ParquetDataset.Open<Person>(narrow, "people/").Select(p => p.Id).ToList();
        var all = ParquetDataset.Open<Person>(wide, "people/").ToList();

        await Assert.That(ids.Count).IsEqualTo(all.Count);
        
        // Files this small are mostly footer and a block at a time, so the saving is less than the share of the columns.
        await Assert.That(narrow.BytesRead).IsLessThan(wide.BytesRead * 6 / 10);
    }

    [Test]
    public async Task TheNumberOfRows_IsAnsweredFromTheFooters()
    {
        var storage = Setup();

        int count = ParquetDataset.Open<Person>(storage, "people/").Count();

        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        
        // The beginning and the end of each file: its first block and its tail.
        await Assert.That(storage.Requests.Count).IsEqualTo(Files * 2);
        await Assert.That(storage.BytesRead).IsLessThan(FilesSize(storage) / 2);
    }

    [Test]
    public async Task ThePlan_SaysWhichFilesAreSkipped()
    {
        var storage = Setup();

        var plan = ParquetDataset.Open<Person>(storage, "people/").Where(p => p.Id >= 2000 && p.Id < 2150).Explain();

        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(plan.RowGroupsSkipped).IsEqualTo(2);
    }

    [Test]
    public async Task OnlyTheFilesThatMatchThePatternAndThePrefix_AreRead()
    {
        var storage = Setup();
        storage.Put("people/folder/deeper.parquet", Setup().ListAsync("people/").ToBlockingEnumerable().Count() > 0 ? ReadAll(Setup(), "people/people-0.parquet") : []);

        var all = ParquetDataset.Open<Person>(storage, "people/").Count();
        var top = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { Recursive = false }).Count();
        var one = ParquetDataset.Open<Person>(storage, "people/people-3").Count();
        var pattern = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { SearchPattern = "people-1*" }).Count();

        await Assert.That(all).IsEqualTo((Files + 1) * RowsPerFile);
        await Assert.That(top).IsEqualTo(Files * RowsPerFile);
        await Assert.That(one).IsEqualTo(RowsPerFile);
        await Assert.That(pattern).IsEqualTo(RowsPerFile);
    }

    [Test]
    public async Task APrefixWithNoFiles_IsAnEmptyDataset()
    {
        var storage = Setup();

        var rows = ParquetDataset.Open<Person>(storage, "nothing/").ToList();

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task TheAsynchronousMethods_Work()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/");

        var list = await people.Where(p => p.Id >= 3000 && p.Id < 3010).ToListAsync();
        var count = await people.CountAsync();
        var any = await people.Where(p => p.Id == 1234).AnyAsync();

        await Assert.That(list.Count).IsEqualTo(10);
        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(any).IsTrue();
    }

    [Test]
    public async Task ADatasetOfAStorage_HasThePrefixAsItsPath()
    {
        var storage = Setup();

        await Assert.That(ParquetDataset.Open<Person>(storage, "people/").Path).IsEqualTo("people/");
        await Assert.That(() => ParquetDataset.Open<Person>(null!, "people/")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TheReadStatistics_AreWhatWasAskedOfTheStorage()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/");

        var rows = people.Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => p.Id).ToList();

        var statistics = people.ReadStatistics;
        await Assert.That(rows.Count).IsEqualTo(150);
        await Assert.That(statistics.Requests).IsEqualTo(storage.Requests.Count);
        await Assert.That(statistics.BytesRead).IsEqualTo(storage.BytesRead);
        await Assert.That(statistics.Listings).IsEqualTo(storage.Listings);
        await Assert.That(statistics.ObjectsOpened).IsEqualTo(Files);
        await Assert.That(statistics.BytesRead).IsLessThan(FilesSize(storage) / 2);
    }

    [Test]
    public async Task AQueryThatReadsMore_CountsMore_AndResetMeasuresOneQuery()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/");

        people.Where(p => p.Id >= 2000 && p.Id < 2010).ToList();
        long narrow = people.ReadStatistics.BytesRead;
        people.ReadStatistics.Reset();
        await Assert.That(people.ReadStatistics.BytesRead).IsEqualTo(0);

        people.ToList();
        long everything = people.ReadStatistics.BytesRead;
        people.ReadStatistics.Reset();
        people.Count();

        await Assert.That(narrow).IsGreaterThan(0);
        await Assert.That(everything).IsGreaterThan(narrow);
        await Assert.That(people.ReadStatistics.BytesRead).IsLessThan(everything / 2);
    }

    [Test]
    public async Task TheCountsAddUp_OverQueries_AndExplainReadsFootersWhichCount()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/");

        people.Where(p => p.Id == 1).ToList();
        long afterQuery = people.ReadStatistics.BytesRead;
        people.Where(p => p.Id == 1).Explain();

        await Assert.That(people.ReadStatistics.BytesRead).IsGreaterThan(afterQuery);
        await Assert.That(people.ReadStatistics.BytesRead).IsEqualTo(storage.BytesRead);
    }

    [Test]
    public async Task ADatasetOfADirectory_CountsTheFilesItReads()
    {
        using var data = new TestDataset();
        var people = data.Open();

        var ids = people.Where(p => p.Id >= 2000 && p.Id < 2010).Select(p => p.Id).ToList();

        await Assert.That(ids.Count).IsEqualTo(10);
        await Assert.That(people.ReadStatistics.ObjectsOpened).IsGreaterThan(0);
        await Assert.That(people.ReadStatistics.BytesRead).IsGreaterThan(0);
        await Assert.That(people.ReadStatistics.Listings).IsEqualTo(0);
    }

    [Test]
    public async Task AQuery_IsRecordedAsMetrics_WithTheRowsItReadAndWhetherItWasFiltered()
    {
        using var metrics = new ParquetNeo.Diagnostics.ParquetNeoMetrics();
        using var recorder = new ParquetNeo.Tests.Diagnostics.MetricRecorder(metrics.Meter);
        var storage = Setup(new RangeReadStreamOptions { BlockSize = 2 * 1024, TailSize = 6 * 1024, Metrics = metrics });
        var people = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { Metrics = metrics });

        var filtered = people.Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => p.Id).ToList();
        var all = people.Select(p => p.Id).ToList();

        await Assert.That(recorder.Count("parquetneo.query.duration")).IsEqualTo(2);
        await Assert.That(recorder.Count("parquetneo.query.duration", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, true))).IsEqualTo(1);
        await Assert.That(recorder.Count("parquetneo.query.duration", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, false))).IsEqualTo(1);
        await Assert.That(recorder.Sum("parquetneo.query.rows", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, true))).IsGreaterThanOrEqualTo(filtered.Count);
        await Assert.That(recorder.Sum("parquetneo.query.rows", (ParquetNeo.Diagnostics.ParquetNeoMetrics.FilteredTag, false))).IsEqualTo(all.Count);

        // The row groups the filtered query read are those of one file, and the ones of the others were skipped; the query of everything read all of them.
        await Assert.That(recorder.Sum("parquetneo.row_groups.read")).IsGreaterThan(Files * 4);
        await Assert.That(recorder.Sum("parquetneo.row_groups.skipped")).IsGreaterThan(0);

        // And the requests to the storage are the ones the storage saw.
        await Assert.That(recorder.Sum("parquetneo.storage.requests")).IsEqualTo(storage.Requests.Count);
        await Assert.That(recorder.Sum("parquetneo.storage.bytes_read")).IsEqualTo(storage.BytesRead);
    }

    [Test]
    public async Task ACountFromTheFooters_AndAQueryThatStopsEarly_AreRecorded()
    {
        using var metrics = new ParquetNeo.Diagnostics.ParquetNeoMetrics();
        using var recorder = new ParquetNeo.Tests.Diagnostics.MetricRecorder(metrics.Meter);
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { Metrics = metrics });

        int count = people.Count();
        var few = people.Take(3).ToList();

        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(few.Count).IsEqualTo(3);
        await Assert.That(recorder.Count("parquetneo.query.duration")).IsEqualTo(2);
        await Assert.That(recorder.Of("parquetneo.query.rows").Min(m => m.Value)).IsEqualTo(0);
        await Assert.That(recorder.Of("parquetneo.query.rows").Max(m => m.Value)).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task ADatasetWithoutMetrics_RecordsNothing()
    {
        using var metrics = new ParquetNeo.Diagnostics.ParquetNeoMetrics();
        using var recorder = new ParquetNeo.Tests.Diagnostics.MetricRecorder(metrics.Meter);

        ParquetDataset.Open<Person>(Setup(), "people/").Where(p => p.Id < 10).ToList();

        await Assert.That(recorder.All.Count).IsEqualTo(0);
    }

    private static byte[] ReadAll(InMemoryStorage storage, string key)
    {
        using var stream = storage.OpenReadAsync(key).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Test]
    public async Task AQuery_IsASpan_WithTheRowGroupsAndTheRequestsItCausesBelowIt()
    {
        using var tracing = new ParquetNeo.Diagnostics.ParquetNeoTracing();
        using var recorder = new ParquetNeo.Tests.Diagnostics.TraceRecorder(tracing.Source);
        var storage = Setup(new RangeReadStreamOptions { BlockSize = 2 * 1024, TailSize = 6 * 1024, Tracing = tracing });
        var people = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { Tracing = tracing, MaxDegreeOfParallelism = 1 });
        using var own = new System.Diagnostics.ActivitySource("test.own");
        using var ownRecorder = new ParquetNeo.Tests.Diagnostics.TraceRecorder(own);

        List<int> ids;
        using (var parent = own.StartActivity("mine"))
        {
            ids = people.Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => p.Id).ToList();

            await Assert.That(System.Diagnostics.Activity.Current).IsEqualTo(parent);
        }

        var query = recorder.Of(ParquetNeo.Diagnostics.ParquetNeoTracing.QuerySpan).Single();
        var groups = recorder.Of(ParquetNeo.Diagnostics.ParquetNeoTracing.RowGroupSpan);
        var requests = recorder.Of(ParquetNeo.Diagnostics.ParquetNeoTracing.StorageRequestSpan);
        await Assert.That((bool)query.GetTagItem(ParquetNeo.Diagnostics.ParquetNeoTracing.FilteredTag)!).IsTrue();
        await Assert.That((long)query.GetTagItem(ParquetNeo.Diagnostics.ParquetNeoTracing.QueryRowsTag)!).IsGreaterThanOrEqualTo(ids.Count);
        await Assert.That(query.ParentSpanId).IsEqualTo(ownRecorder.Spans.Single().SpanId);
        await Assert.That(groups.Count).IsGreaterThan(0);
        await Assert.That(groups.All(span => span.ParentSpanId == query.SpanId)).IsTrue();
        await Assert.That(requests.Count).IsEqualTo(storage.Requests.Count);
        await Assert.That(requests.All(span => span.TraceId == query.TraceId)).IsTrue();
    }

    [Test]
    public async Task AQueryThatStopsEarly_AndACountFromTheFooters_AreEachASpan()
    {
        using var tracing = new ParquetNeo.Diagnostics.ParquetNeoTracing();
        using var recorder = new ParquetNeo.Tests.Diagnostics.TraceRecorder(tracing.Source);
        var people = ParquetDataset.Open<Person>(Setup(), "people/", new ParquetDatasetOptions { Tracing = tracing, MaxDegreeOfParallelism = 1 });

        var few = people.Take(3).ToList();
        int count = people.Count();
        long counted = await people.CountAsync();

        await Assert.That(few.Count).IsEqualTo(3);
        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(counted).IsEqualTo(Files * RowsPerFile);
        var queries = recorder.Of(ParquetNeo.Diagnostics.ParquetNeoTracing.QuerySpan);
        await Assert.That(queries.Count).IsEqualTo(3);
        await Assert.That((long)queries[0].GetTagItem(ParquetNeo.Diagnostics.ParquetNeoTracing.QueryRowsTag)!).IsGreaterThanOrEqualTo(3);
        await Assert.That(queries.All(span => (bool)span.GetTagItem(ParquetNeo.Diagnostics.ParquetNeoTracing.FilteredTag)! == false)).IsTrue();
    }
}
