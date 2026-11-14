using System.Runtime.CompilerServices;
using ParquetNeo.Storage;

namespace ParquetNeo.Tests.Storage;

/// <summary>A storage that keeps objects in memory and records every range of them that is read, to see how much of a file a read touches.</summary>
public sealed class InMemoryStorage : IParquetStorage
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>The ranges that were read, in order.</summary>
    public List<StorageRequest> Requests { get; } = [];

    /// <summary>How many times objects were listed.</summary>
    public int Listings { get; private set; }

    /// <summary>When set, a read gives at most this many bytes, as a network may, so that readers have to ask again.</summary>
    public int? MaxBytesPerRead { get; set; }

    public RangeReadStreamOptions? Options { get; set; }

    public long BytesRead => Requests.Sum(request => request.Count);

    public InMemoryStorage Put(string key, byte[] data)
    {
        _objects[key] = data;
        return this;
    }

    public void Remove(string key) => _objects.Remove(key);

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        Listings++;
        return _objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(pair => new StorageObject(pair.Key, pair.Value.Length))
            .ToAsyncEnumerable();
    }

    public ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _objects.TryGetValue(key, out var data)
            ? ValueTask.FromResult<Stream>(new ObjectStream(this, key, data))
            : throw new FileNotFoundException($"There is no object '{key}'.", key);

    private sealed class ObjectStream(InMemoryStorage storage, string key, byte[] data) : RangeReadStream(data.Length, storage.Options)
    {
        protected override ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            int count = Math.Min(destination.Length, storage.MaxBytesPerRead ?? int.MaxValue);
            storage.Requests.Add(new StorageRequest(key, offset, count));
            data.AsMemory((int)offset, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
    }
}
