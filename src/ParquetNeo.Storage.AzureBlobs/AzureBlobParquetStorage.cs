using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ParquetNeo.Storage;

namespace ParquetNeo.Storage.AzureBlobs;

/// <summary>
/// The Parquet files of a container of Azure Blob Storage (or of the Azurite emulator), for datasets and for the asynchronous methods of
/// the serializer. Only the byte ranges that are read are requested, so a file is read for its footer and the column chunks of the row
/// groups that a filter does not rule out.
/// </summary>
/// <remarks>
/// How to log in (a connection string, a shared key, a token of Microsoft Entra ID) is the setting of the container client you give: create it as
/// you would for any use of the Azure SDK. Reading needs the role <c>Storage Blob Data Reader</c>, or the permissions to list and to read.
/// </remarks>
/// <param name="container">The client of the container; it needs nothing from the storage but what it is given.</param>
/// <param name="options">How the files are read in blocks, or the defaults when not set.</param>
public sealed class AzureBlobParquetStorage(BlobContainerClient container, RangeReadStreamOptions? options = null) : IParquetStorage
{
    private readonly BlobContainerClient _container = container ?? throw new ArgumentNullException(nameof(container));

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ListBlobsAsync(prefix, cancellationToken);
    }

    public async ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var blob = _container.GetBlobClient(key);
        try
        {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            return new AzureBlobRangeStream(blob, properties.ContentLength, properties.ETag, options);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            throw new FileNotFoundException($"There is no blob '{key}' in the container '{_container.Name}'.", key, exception);
        }
    }

    private async IAsyncEnumerable<StorageObject> ListBlobsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken).ConfigureAwait(false))
        {
            yield return new StorageObject(item.Name, item.Properties.ContentLength ?? 0);
        }
    }
}
