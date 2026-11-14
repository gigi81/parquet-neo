using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using ParquetNeo.Storage;

namespace ParquetNeo.Storage.S3;

/// <summary>An object of S3 as a stream: a request with a <c>Range</c> header for each read, which is tied to the version of the object that was opened.</summary>
internal sealed class S3RangeStream(IAmazonS3 client, string bucket, string key, long length, string? etag, RangeReadStreamOptions? options)
    : RangeReadStream(length, options)
{
    protected override async ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(offset, offset + destination.Length - 1),

            // The same version of the object, or an error: not a file made of parts of two.
            EtagToMatch = etag,
        };

        try
        {
            using var response = await client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return await response.ResponseStream.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException($"The object '{key}' of the bucket '{bucket}' changed while it was read.", exception);
        }
    }
}
