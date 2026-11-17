using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ParquetNeo.Diagnostics;
using ParquetNeo.Serialization;
using ParquetNeo.Storage;

namespace ParquetNeo.Linq;

/// <summary>The files of a dataset, and how rows are read from them.</summary>
internal sealed class DatasetSource(string path, ParquetDatasetOptions options, IParquetStorage? storage = null)
{
    /// <summary>The file or directory, or the prefix of the keys in the storage.</summary>
    public string Path { get; } = path;

    public ParquetDatasetOptions Options { get; } = options;

    /// <summary>What was read from the files and the storage by every query of the dataset.</summary>
    public ReadStatistics Statistics { get; } = new();

    private IParquetStorage? _metered;

    /// <summary>What files are opened and listed through: the storage, or the disk, counting what is read from it.</summary>
    private IParquetStorage Metered => _metered ??= new MeteredStorage(storage ?? new PathStorage(), Statistics);

    /// <summary>The files, in the order of their names.</summary>
    /// <exception cref="FileNotFoundException">The path is neither a file nor a directory.</exception>
    public IReadOnlyList<string> Files() => storage is not null ? StorageFiles(Metered) : LocalFiles();

    /// <summary>The files, in the order of their names, listing a storage without blocking the caller.</summary>
    /// <exception cref="FileNotFoundException">The path is neither a file nor a directory.</exception>
    public async ValueTask<IReadOnlyList<string>> FilesAsync(CancellationToken cancellationToken)
    {
        if (storage is null)
        {
            return LocalFiles(); // the disk has no asynchronous listing
        }

        var keys = new List<string>();
        await foreach (string key in Metered.ListAsync(Path, cancellationToken).Select(storageObject => storageObject.Key).Where(Matches).ConfigureAwait(false))
        {
            keys.Add(key);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private IReadOnlyList<string> LocalFiles()
    {
        if (File.Exists(Path))
        {
            return [Path];
        }

        if (!Directory.Exists(Path))
        {
            throw new FileNotFoundException($"There is no file or directory '{Path}'.", Path);
        }

        var files = Directory.EnumerateFiles(Path, Options.SearchPattern, Options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>The options of the readers of the files that are read: those of the dataset, with the metrics of the dataset when it has some.</summary>
    private ParquetReaderOptions ReaderOptions =>
        Options.Metrics is null ? Options.Deserializer.Reader : Options.Deserializer.Reader with { Metrics = Options.Metrics };

    /// <summary>Opens a file of the list of <see cref="Files"/> for reading, from the disk or from the storage.</summary>
    public Stream OpenFile(string file) => Metered.OpenReadAsync(file).AsTask().GetAwaiter().GetResult();

    private ValueTask<Stream> OpenFileAsync(string file, CancellationToken cancellationToken) => Metered.OpenReadAsync(file, cancellationToken);

    /// <summary>The keys below the prefix that match the search pattern (and are not in a folder below it, when the dataset does not look there), in the order of their names.</summary>
    private List<string> StorageFiles(IParquetStorage source)
    {
        var keys = new List<string>();
        foreach (string key in source.ListAsync(Path).Select(storageObject => storageObject.Key).Where(Matches).ToBlockingEnumerable())
        {
            keys.Add(key);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>Whether a key below the prefix is a file of the dataset: its name matches the search pattern, and it is not in a folder below the prefix when the dataset does not look there.</summary>
    private bool Matches(string key)
    {
        string name = key[(key.LastIndexOf('/') + 1)..];
        bool inFolder = key.AsSpan(Path.Length).Contains('/');
        return FileSystemName.MatchesSimpleExpression(Options.SearchPattern, name) && (Options.Recursive || !inFolder);
    }

    /// <summary>The rows of the row groups of the files that the filter does not rule out, files in parallel.</summary>
    public IEnumerable<TRow> Read<TRow>(ParquetFilter? filter)
    {
        var rows = ReadFiles<TRow>(filter);
        return Options.Metrics is { } metrics ? Measured(rows, metrics, filter is not null) : rows;
    }

    /// <summary>
    /// The rows, counting them and timing the query from the first row asked for until the last one is given or the enumeration is disposed
    /// (a query that takes the first rows ends there), into the metrics.
    /// </summary>
    internal static IEnumerable<TRow> Measured<TRow>(IEnumerable<TRow> rows, ParquetNeoMetrics metrics, bool filtered)
    {
        long started = Stopwatch.GetTimestamp();
        long count = 0;
        try
        {
            foreach (var row in rows)
            {
                count++;
                yield return row;
            }
        }
        finally
        {
            metrics.RecordQuery(Stopwatch.GetElapsedTime(started).TotalSeconds, count, filtered);
        }
    }

    private IEnumerable<TRow> ReadFiles<TRow>(ParquetFilter? filter)
    {
        var files = Files();
        int degree = Math.Min(Options.MaxDegreeOfParallelism, files.Count);
        if (degree <= 1)
        {
            return files.SelectMany(file => ReadFile<TRow>(file, filter));
        }

        return Unwrapped(files.AsParallel()
            .AsUnordered()
            .WithDegreeOfParallelism(degree)
            .WithMergeOptions(ParallelMergeOptions.NotBuffered)
            .SelectMany(file => ReadFile<TRow>(file, filter)));
    }

    /// <summary>
    /// The rows of the row groups of the files that the filter does not rule out, like <see cref="Read{TRow}"/> but read with asynchronous
    /// calls: no thread waits for a file. The files are read by up to <see cref="ParquetDatasetOptions.MaxDegreeOfParallelism"/> readers at a time,
    /// whose rows go through a channel of a bounded size, so a slow consumer holds the readers back; stopping the enumeration stops them.
    /// </summary>
    public IAsyncEnumerable<TRow> ReadAsync<TRow>(ParquetFilter? filter, CancellationToken cancellationToken = default)
    {
        var rows = ReadFilesAsync<TRow>(filter, cancellationToken);
        return Options.Metrics is { } metrics ? MeasuredAsync(rows, metrics, filter is not null, cancellationToken) : rows;
    }

    /// <summary>The asynchronous form of <see cref="Measured{TRow}"/>.</summary>
    internal static async IAsyncEnumerable<TRow> MeasuredAsync<TRow>(IAsyncEnumerable<TRow> rows, ParquetNeoMetrics metrics, bool filtered, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        long count = 0;
        try
        {
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                count++;
                yield return row;
            }
        }
        finally
        {
            metrics.RecordQuery(Stopwatch.GetElapsedTime(started).TotalSeconds, count, filtered);
        }
    }

    private async IAsyncEnumerable<TRow> ReadFilesAsync<TRow>(ParquetFilter? filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var files = await FilesAsync(cancellationToken).ConfigureAwait(false);
        int degree = Math.Max(1, Math.Min(Options.MaxDegreeOfParallelism, files.Count));
        var channel = Channel.CreateBounded<TRow>(new BoundedChannelOptions(1024) { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? failure = null;
        int next = -1;
        var readers = new Task[degree];
        for (int reader = 0; reader < degree; reader++)
        {
            readers[reader] = Task.Run(
                async () =>
                {
                    try
                    {
                        int index;
                        while ((index = Interlocked.Increment(ref next)) < files.Count)
                        {
                            await foreach (var row in ReadFileAsync<TRow>(files[index], filter, stop.Token).ConfigureAwait(false))
                            {
                                await channel.Writer.WriteAsync(row, stop.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
        }

        var finished = Task.WhenAll(readers).ContinueWith(_ => channel.Writer.Complete(failure?.SourceException), TaskScheduler.Default);
        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<TRow> ReadFileAsync<TRow>(string file, ParquetFilter? filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await OpenFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (filter is not null)
        {
            await using (var reader = await ParquetFileReader.OpenAsync(stream, Options.Deserializer.Reader, leaveOpen: true, cancellationToken).ConfigureAwait(false))
            {
                if ((await MatchingRowGroupsAsync(reader, filter, cancellationToken).ConfigureAwait(false)).Count == 0)
                {
                    yield break;
                }
            }

            stream.Position = 0;
        }

        await foreach (var row in ParquetSerializer.DeserializeAllAsync<TRow>(stream, Options.Deserializer with { RowGroupFilter = filter, Reader = ReaderOptions }, cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>The number of rows in all the files, from their footers, read without blocking.</summary>
    public async ValueTask<long> CountRowsAsync(CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (string file in await FilesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var stream = await OpenFileAsync(file, cancellationToken).ConfigureAwait(false);
            await using var reader = await ParquetFileReader.OpenAsync(stream, Options.Deserializer.Reader, leaveOpen: true, cancellationToken).ConfigureAwait(false);
            rows += reader.RowCount;
        }

        return rows;
    }

    /// <summary>How many files and row groups a filter rules out, read without blocking.</summary>
    public async ValueTask<(int Files, int FilesSkipped, int RowGroups, int RowGroupsSkipped)> InspectAsync(ParquetFilter? filter, CancellationToken cancellationToken)
    {
        var files = await FilesAsync(cancellationToken).ConfigureAwait(false);
        int skippedFiles = 0;
        int groups = 0;
        int skippedGroups = 0;
        foreach (string file in files)
        {
            await using var stream = await OpenFileAsync(file, cancellationToken).ConfigureAwait(false);
            await using var reader = await ParquetFileReader.OpenAsync(stream, Options.Deserializer.Reader, leaveOpen: true, cancellationToken).ConfigureAwait(false);
            int kept = (await MatchingRowGroupsAsync(reader, filter, cancellationToken).ConfigureAwait(false)).Count;
            if (kept == 0)
            {
                skippedFiles++;
                continue;
            }

            groups += reader.RowGroupCount;
            skippedGroups += reader.RowGroupCount - kept;
        }

        return (files.Count, skippedFiles, groups, skippedGroups);
    }

    private static async ValueTask<IReadOnlyList<int>> MatchingRowGroupsAsync(ParquetFileReader reader, ParquetFilter? filter, CancellationToken cancellationToken)
    {
        if (filter is not null)
        {
            try
            {
                return await reader.GetMatchingRowGroupsAsync(filter, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // A value that does not suit the column decides nothing; the rows are checked as they are read.
            }
        }

        return Enumerable.Range(0, reader.RowGroupCount).ToArray();
    }

    /// <summary>The number of rows in all the files, from their footers.</summary>
    public long CountRows()
    {
        long rows = 0;
        foreach (string file in Files())
        {
            using var stream = OpenFile(file);
            using var reader = ParquetFileReader.Open(stream, Options.Deserializer.Reader, leaveOpen: true);
            rows += reader.RowCount;
        }

        return rows;
    }

    /// <summary>How many files and row groups a filter rules out.</summary>
    public (int Files, int FilesSkipped, int RowGroups, int RowGroupsSkipped) Inspect(ParquetFilter? filter)
    {
        var files = Files();
        int skippedFiles = 0;
        int groups = 0;
        int skippedGroups = 0;
        foreach (string file in files)
        {
            using var stream = OpenFile(file);
            using var reader = ParquetFileReader.Open(stream, Options.Deserializer.Reader, leaveOpen: true);
            int kept = MatchingRowGroups(reader, filter).Count;
            if (kept == 0)
            {
                skippedFiles++;
                continue;
            }

            groups += reader.RowGroupCount;
            skippedGroups += reader.RowGroupCount - kept;
        }

        return (files.Count, skippedFiles, groups, skippedGroups);
    }

    /// <summary>The row groups of a file a filter does not rule out; all of them when the filter cannot be checked against the file.</summary>
    private static IReadOnlyList<int> MatchingRowGroups(ParquetFileReader reader, ParquetFilter? filter)
    {
        if (filter is not null)
        {
            try
            {
                return reader.GetMatchingRowGroups(filter);
            }
            catch (ArgumentException)
            {
                // A value that does not suit the column (a type of the user's own, for example) decides nothing; the rows are checked as they are read.
            }
        }

        return Enumerable.Range(0, reader.RowGroupCount).ToArray();
    }

    private IEnumerable<TRow> ReadFile<TRow>(string file, ParquetFilter? filter)
    {
        using var stream = OpenFile(file);
        ParquetFilter? effective = filter;
        if (filter is not null)
        {
            using var reader = ParquetFileReader.Open(stream, Options.Deserializer.Reader, leaveOpen: true);
            if (MatchingRowGroups(reader, filter).Count == 0)
            {
                yield break;
            }

            stream.Position = 0;
        }

        foreach (var row in ParquetSerializer.DeserializeAll<TRow>(stream, Options.Deserializer with { RowGroupFilter = effective, Reader = ReaderOptions }))
        {
            yield return row;
        }
    }

    /// <summary>Gives the exception of a failed read as it is, instead of wrapped in the <see cref="AggregateException"/> a parallel query throws.</summary>
    private static IEnumerable<TRow> Unwrapped<TRow>(IEnumerable<TRow> rows)
    {
        using var enumerator = rows.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            }

            yield return enumerator.Current;
        }
    }

    /// <summary>The files of a disk, by their paths, to be counted like those of a storage.</summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "A stand-in for opening files; the dataset lists the files of a disk itself, so listing is never asked of it.")]
    private sealed class PathStorage : IParquetStorage
    {
        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The files of a disk are listed by the dataset.");

        public ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(File.OpenRead(key));
    }
}
