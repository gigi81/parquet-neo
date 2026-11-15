namespace ParquetNeo.Storage;

/// <summary>
/// A storage that counts what is read from another into <see cref="ReadStatistics"/>: for a file of a bucket or a container, or for a dataset, to see
/// how much a query or a filter costs. The streams it opens count the requests that their storage makes (and not the reads of the code that uses them,
/// which a block that is kept answers), so the numbers are those of the network.
/// </summary>
/// <param name="inner">The storage that is read.</param>
/// <param name="statistics">Where the counts go, or a new one when not set.</param>
public sealed class MeteredStorage(IParquetStorage inner, ReadStatistics? statistics = null) : IParquetStorage
{
    private readonly IParquetStorage _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The counts of everything read through this storage.</summary>
    public ReadStatistics Statistics { get; } = statistics ?? new ReadStatistics();

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        Statistics.AddListing();
        return _inner.ListAsync(prefix, cancellationToken);
    }

    public async ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var stream = await _inner.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        Statistics.AddObjectOpened();
        if (stream is RangeReadStream ranges)
        {
            ranges.Statistics = Statistics;
            return ranges;
        }

        return new CountingStream(stream, Statistics);
    }
}
