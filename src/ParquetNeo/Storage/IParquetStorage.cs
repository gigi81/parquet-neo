namespace ParquetNeo.Storage;

/// <summary>
/// Where Parquet files are kept: a folder on a disk, a bucket or a container of an object storage. It lists the files and opens one
/// for reading, which is all that datasets and the asynchronous methods of the serializer need, and they read only the parts of a
/// file they use.
/// </summary>
public interface IParquetStorage
{
    /// <summary>The objects whose key starts with <paramref name="prefix"/>, with the ones below a folder too, in no particular order.</summary>
    IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an object for reading as a stream that can seek, and reads from the storage only the parts of the object that are read from
    /// it. Dispose the stream when done. A stream is not safe for use from several threads at once.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no such object.</exception>
    ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);
}
