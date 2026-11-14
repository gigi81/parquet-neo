using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using ParquetNeo.Storage;

namespace ParquetNeo.Storage.S3;

/// <summary>
/// The Parquet files of a bucket of Amazon S3 (or of a storage that speaks its protocol, such as MinIO), for datasets and for the
/// asynchronous methods of the serializer. Only the byte ranges that are read are requested, so a file is read for its footer and the
/// column chunks of the row groups that a filter does not rule out.
/// </summary>
/// <remarks>
/// How to log in, which region and which endpoint are the settings of the client you give: create it as you would for any use of the AWS SDK.
/// Reading needs the permissions <c>s3:ListBucket</c> and <c>s3:GetObject</c>.
/// </remarks>
/// <param name="client">The client of the AWS SDK; it is not disposed with the storage.</param>
/// <param name="bucket">The name of the bucket.</param>
/// <param name="options">How the files are read in blocks, or the defaults when not set.</param>
public sealed class S3ParquetStorage(IAmazonS3 client, string bucket, RangeReadStreamOptions? options = null) : IParquetStorage
{
    private readonly IAmazonS3 _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string _bucket = string.IsNullOrEmpty(bucket) ? throw new ArgumentException("The bucket needs a name.", nameof(bucket)) : bucket;

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ListObjectsAsync(prefix, cancellationToken);
    }

    private async IAsyncEnumerable<StorageObject> ListObjectsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return new StorageObject(item.Key, item.Size ?? 0);
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true && !string.IsNullOrEmpty(response.NextContinuationToken));
    }

    public async ValueTask<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        GetObjectMetadataResponse metadata;
        try
        {
            metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"There is no object '{key}' in the bucket '{_bucket}'.", key, exception);
        }

        return new S3RangeStream(_client, _bucket, key, metadata.ContentLength, metadata.ETag, options);
    }
}
