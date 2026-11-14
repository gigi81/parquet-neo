using System.Runtime.ExceptionServices;
using ParquetNeo.Serialization;

namespace ParquetNeo.Linq;

/// <summary>The files of a dataset, and how rows are read from them.</summary>
internal sealed class DatasetSource(string path, ParquetDatasetOptions options)
{
    public string Path { get; } = path;

    public ParquetDatasetOptions Options { get; } = options;

    /// <summary>The files, in the order of their names.</summary>
    /// <exception cref="FileNotFoundException">The path is neither a file nor a directory.</exception>
    public IReadOnlyList<string> Files()
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

    /// <summary>The rows of the row groups of the files that the filter does not rule out, files in parallel.</summary>
    public IEnumerable<TRow> Read<TRow>(ParquetFilter? filter)
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

    /// <summary>The number of rows in all the files, from their footers.</summary>
    public long CountRows()
    {
        long rows = 0;
        foreach (string file in Files())
        {
            using var stream = File.OpenRead(file);
            using var reader = new ParquetFileReader(stream, Options.Deserializer.Reader, leaveOpen: true);
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
            using var stream = File.OpenRead(file);
            using var reader = new ParquetFileReader(stream, Options.Deserializer.Reader, leaveOpen: true);
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
        using var stream = File.OpenRead(file);
        ParquetFilter? effective = filter;
        if (filter is not null)
        {
            using var reader = new ParquetFileReader(stream, Options.Deserializer.Reader, leaveOpen: true);
            if (MatchingRowGroups(reader, filter).Count == 0)
            {
                yield break;
            }

            stream.Position = 0;
        }

        foreach (var row in ParquetSerializer.DeserializeAll<TRow>(stream, Options.Deserializer with { RowGroupFilter = effective }))
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
}
