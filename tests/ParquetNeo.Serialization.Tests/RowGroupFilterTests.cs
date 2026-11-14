namespace ParquetNeo.Serialization.Tests;

/// <summary>The serializer skips the row groups a filter rules out, and does not fetch their bytes.</summary>
public class RowGroupFilterTests
{
    public sealed class Reading
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public double? Value { get; set; }
    }

    /// <summary>A stream that counts the bytes read from it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }

    /// <summary>Five row groups of 100 rows: the ids 0 to 99, 100 to 199, and so on.</summary>
    private static byte[] File()
    {
        var rows = Enumerable.Range(0, 500).Select(i => new Reading { Id = i, Name = $"name-{i}-{new string('x', 40)}", Value = i % 3 == 0 ? null : i * 0.5 }).ToList();
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = 100 });
        return stream.ToArray();
    }

    private static ParquetDeserializerOptions Filtered(ParquetFilter filter) => new() { RowGroupFilter = filter };

    [Test]
    public async Task AllTheRowsAreRead_WithoutAFilter()
    {
        await Assert.That(ParquetSerializer.Deserialize<Reading>(new MemoryStream(File())).Count).IsEqualTo(500);
    }

    [Test]
    public async Task TheRowGroupsTheFilterRulesOut_AreNotRead_AndTheRowsOfTheOthersAllAre()
    {
        var rows = ParquetSerializer.Deserialize<Reading>(new MemoryStream(File()), Filtered(ParquetFilter.Column("Id").GreaterThanOrEqual(250)));

        // The row groups of 200 to 299, 300 to 399 and 400 to 499: whole, though only some of the rows satisfy the filter.
        await Assert.That(rows.Count).IsEqualTo(300);
        await Assert.That(rows.Min(r => r.Id)).IsEqualTo(200);
        await Assert.That(rows.Max(r => r.Id)).IsEqualTo(499);
    }

    [Test]
    public async Task TheRowsAreTheSame_WhicheverWayTheyAreAskedFor()
    {
        byte[] file = File();
        var options = Filtered(ParquetFilter.Column("Id").Between(120, 130).Or(ParquetFilter.Column("Id").Equal(420)));

        var all = ParquetSerializer.Deserialize<Reading>(new MemoryStream(file), options).Select(r => r.Id).ToList();
        var each = ParquetSerializer.DeserializeAll<Reading>(new MemoryStream(file), options).Select(r => r.Id).ToList();
        var async = (await ParquetSerializer.DeserializeAsync<Reading>(new MemoryStream(file), options)).Select(r => r.Id).ToList();
        var asyncEach = new List<int>();
        await foreach (var row in ParquetSerializer.DeserializeAllAsync<Reading>(new MemoryStream(file), options))
        {
            asyncEach.Add(row.Id);
        }

        await Assert.That(all.Count).IsEqualTo(200);
        await Assert.That(all.Take(100)).IsEquivalentTo(Enumerable.Range(100, 100));
        await Assert.That(all.Skip(100)).IsEquivalentTo(Enumerable.Range(400, 100));
        await Assert.That(each).IsEquivalentTo(all);
        await Assert.That(async).IsEquivalentTo(all);
        await Assert.That(asyncEach).IsEquivalentTo(all);
    }

    [Test]
    public async Task AFilterThatRulesOutEveryRowGroup_GivesNoRows()
    {
        var rows = ParquetSerializer.Deserialize<Reading>(new MemoryStream(File()), Filtered(ParquetFilter.Column("Id").GreaterThan(10_000)));

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task AFilterOnAColumnTheFileDoesNotHave_KeepsEveryRowGroup()
    {
        var rows = ParquetSerializer.Deserialize<Reading>(new MemoryStream(File()), Filtered(ParquetFilter.Column("Missing").Equal(1)));

        await Assert.That(rows.Count).IsEqualTo(500);
    }

    [Test]
    public async Task TheAsynchronousMethods_DoNotFetchTheBytesOfARowGroupThatIsSkipped()
    {
        byte[] file = File();
        var everything = new CountingStream(new MemoryStream(file));
        var one = new CountingStream(new MemoryStream(file));

        int all = (await ParquetSerializer.DeserializeAsync<Reading>(everything)).Count;
        int some = (await ParquetSerializer.DeserializeAsync<Reading>(one, Filtered(ParquetFilter.Column("Id").Equal(333)))).Count;

        await Assert.That(all).IsEqualTo(500);
        await Assert.That(some).IsEqualTo(100);

        // The footer and one row group of five, so well under half of what the whole file takes.
        await Assert.That(one.BytesRead).IsLessThan(everything.BytesRead / 2);
    }

    [Test]
    public async Task ANullCountOnAnOptionalColumn_IsUsedToo()
    {
        // Every third row has no Value, so every row group has some nulls and some numbers.
        byte[] file = File();

        var withNulls = ParquetSerializer.Deserialize<Reading>(new MemoryStream(file), Filtered(ParquetFilter.Column("Value").IsNull()));
        var allNumbers = ParquetSerializer.Deserialize<Reading>(new MemoryStream(file), Filtered(ParquetFilter.Column("Value").IsNotNull().Not().Not() & ParquetFilter.Column("Value").GreaterThan(1000)));

        await Assert.That(withNulls.Count).IsEqualTo(500);
        await Assert.That(allNumbers).IsEmpty();
    }

    /// <summary>Five row groups of 100 rows whose ids are spread over the whole range: group g has the ids that leave g when divided by 5.</summary>
    private static byte[] Spread(bool bloomFilters)
    {
        var rows = Enumerable.Range(0, 500).OrderBy(i => i % 5).ThenBy(i => i).Select(i => new Reading { Id = i, Name = $"name-{i}", Value = i }).ToList();
        var writer = bloomFilters ? new ParquetWriterOptions { ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["Id"] = new() { FalsePositiveRate = 0.0001 } } } : new ParquetWriterOptions();
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = 100, Writer = writer });
        return stream.ToArray();
    }

    [Test]
    public async Task TheBloomFiltersOfAFile_RuleOutTheRowGroupsTheStatisticsCannot()
    {
        var options = Filtered(ParquetFilter.Column("Id").Equal(233));

        var withFilters = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Spread(bloomFilters: true)), options);
        var without = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Spread(bloomFilters: false)), options);

        // 233 is in the range of every row group; only the filters know it is in one.
        await Assert.That(withFilters.Count).IsEqualTo(100);
        await Assert.That(withFilters.All(r => r.Id % 5 == 3)).IsTrue();
        await Assert.That(without.Count).IsEqualTo(500);
    }

    [Test]
    public async Task TheRowsAreTheSame_WithBloomFiltersAsWithout_OnceTheFilterIsAppliedToThem()
    {
        var filter = ParquetFilter.Column("Id").In(7, 12, 499, 1000);

        var withFilters = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Spread(bloomFilters: true)), Filtered(filter)).Where(r => r.Id is 7 or 12 or 499 or 1000).Select(r => r.Id).Order().ToList();
        var without = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Spread(bloomFilters: false)), Filtered(filter)).Where(r => r.Id is 7 or 12 or 499 or 1000).Select(r => r.Id).Order().ToList();

        await Assert.That(withFilters).IsEquivalentTo(new[] { 7, 12, 499 });
        await Assert.That(without).IsEquivalentTo(withFilters);
    }

    [Test]
    public async Task TheAsynchronousMethods_CheckTheStatisticsOnly_AndStillReturnTheRows()
    {
        var rows = await ParquetSerializer.DeserializeAsync<Reading>(new MemoryStream(Spread(bloomFilters: true)), Filtered(ParquetFilter.Column("Id").Equal(233)));

        await Assert.That(rows.Count).IsEqualTo(500);
        await Assert.That(rows.Any(r => r.Id == 233)).IsTrue();
    }

    /// <summary>One row group of 5000 rows in order of Id, in many small pages, with or without a page index.</summary>
    private static byte[] Paged(bool pageIndex)
    {
        var rows = Enumerable.Range(0, 5000).Select(i => new Reading { Id = i, Name = $"name-{i}", Value = i % 7 == 0 ? null : i }).ToList();
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { WritePageIndex = pageIndex, DataPageSize = 600 } });
        return stream.ToArray();
    }

    [Test]
    public async Task ThePageIndexOfAFile_RulesOutThePagesTheRowGroupStatisticsCannot()
    {
        var options = Filtered(ParquetFilter.Column("Id").GreaterThanOrEqual(1000) & ParquetFilter.Column("Id").LessThan(1100));

        var indexed = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Paged(pageIndex: true)), options);
        var plain = ParquetSerializer.Deserialize<Reading>(new MemoryStream(Paged(pageIndex: false)), options);

        await Assert.That(plain.Count).IsEqualTo(5000);
        await Assert.That(indexed.Count).IsBetween(100, 1000);
        await Assert.That(indexed.Count(r => r.Id is >= 1000 and < 1100)).IsEqualTo(100);
        await Assert.That(indexed.All(r => r.Name == $"name-{r.Id}" && r.Value == (r.Id % 7 == 0 ? null : r.Id))).IsTrue();
    }

    [Test]
    public async Task ThePageIndex_ReadsFewerBytes()
    {
        var options = Filtered(ParquetFilter.Column("Id").Equal(2500));
        var bytes = Paged(pageIndex: true);
        using var all = new CountingStream(new MemoryStream(bytes));
        using var some = new CountingStream(new MemoryStream(bytes));

        ParquetSerializer.Deserialize<Reading>(all, new ParquetDeserializerOptions());
        var rows = ParquetSerializer.Deserialize<Reading>(some, options);

        await Assert.That(rows.Any(r => r.Id == 2500)).IsTrue();
        await Assert.That(some.BytesRead).IsLessThan(all.BytesRead / 5);
    }

    [Test]
    public async Task TheAsynchronousMethods_DoNotUseThePageIndex()
    {
        var rows = await ParquetSerializer.DeserializeAsync<Reading>(new MemoryStream(Paged(pageIndex: true)), Filtered(ParquetFilter.Column("Id").Equal(2500)));

        await Assert.That(rows.Count).IsEqualTo(5000);
    }
}
