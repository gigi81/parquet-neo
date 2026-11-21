namespace ParquetNeo.Storage;

/// <summary>
/// What was read from a storage: how many objects were opened, how many requests were made to it, and how many bytes they asked for. Safe to
/// use from several threads, which is how a dataset reads its files. Counts add up until <see cref="Reset"/>.
/// </summary>
public sealed class ReadStatistics
{
    private long _objectsOpened;
    private long _listings;
    private long _requests;
    private long _bytesRead;

    /// <summary>How many objects were opened for reading.</summary>
    public long ObjectsOpened => Interlocked.Read(ref _objectsOpened);

    /// <summary>How many times the objects of a prefix were listed.</summary>
    public long Listings => Interlocked.Read(ref _listings);

    /// <summary>
    /// How many requests were made to the storage for ranges of the objects (what a bill is for). A stream that is not made of ranges, such as a file of a
    /// disk, counts a request for each of its reads.
    /// </summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>How many bytes of the objects were asked for and received (not the size of the requests and the listings themselves).</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>Sets every count to 0, to measure what comes next. Not atomic: do not reset while something is reading.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _objectsOpened, 0);
        Interlocked.Exchange(ref _listings, 0);
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _bytesRead, 0);
    }

    /// <summary>The counts as they are now, which do not change after.</summary>
    public ReadStatisticsSnapshot Snapshot() => new(ObjectsOpened, Listings, Requests, BytesRead);

    /// <summary>A one line description of the counts.</summary>
    public override string ToString() => Snapshot().ToString();

    internal void AddObjectOpened() => Interlocked.Increment(ref _objectsOpened);

    internal void AddListing() => Interlocked.Increment(ref _listings);

    internal void AddRequest(long bytes)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _bytesRead, bytes);
    }
}

/// <summary>The counts of a <see cref="ReadStatistics"/> at a moment.</summary>
public readonly record struct ReadStatisticsSnapshot(long ObjectsOpened, long Listings, long Requests, long BytesRead)
{
    /// <summary>A one line description of the counts.</summary>
    public override string ToString() =>
        $"{BytesRead:N0} bytes in {Requests:N0} requests, {ObjectsOpened:N0} objects opened, {Listings:N0} listings";
}
