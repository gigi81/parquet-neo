using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ParquetNeo.Storage;

namespace ParquetNeo.Storage.AzureBlobs;

/// <summary>A blob as a stream: a request for a range for each read, which is tied to the version of the blob that was opened.</summary>
internal sealed class AzureBlobRangeStream(BlobClient blob, long length, ETag etag, RangeReadStreamOptions? options)
    : RangeReadStream(length, options)
{
    protected override async ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var request = new BlobDownloadOptions
        {
            Range = new HttpRange(offset, destination.Length),

            // The same version of the blob, or an error: not a file made of parts of two.
            Conditions = new BlobRequestConditions { IfMatch = etag },
        };

        try
        {
            using var result = (await blob.DownloadStreamingAsync(request, cancellationToken).ConfigureAwait(false)).Value;
            return await result.Content.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            throw new IOException($"The blob '{blob.Name}' of the container '{blob.BlobContainerName}' changed while it was read.", exception);
        }
    }
}
