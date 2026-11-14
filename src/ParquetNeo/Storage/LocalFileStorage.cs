namespace ParquetNeo.Storage;

/// <summary>A folder of a disk as a storage: the keys are the paths of the files below it, with <c>/</c> between the parts.</summary>
/// <param name="root">The folder.</param>
public sealed class LocalFileStorage(string root) : IParquetStorage
{
    private readonly string _root = Path.GetFullPath(root);

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return Objects(prefix).ToAsyncEnumerable();
    }

    public ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The key '{key}' is not below the folder of the storage.", nameof(key));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no object '{key}'.", key);
        }

        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess));
    }

    private IEnumerable<StorageObject> Objects(string prefix)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            string key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new StorageObject(key, new FileInfo(file).Length);
            }
        }
    }
}
