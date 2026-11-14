using System.Net;
using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ParquetNeo.Storage.S3.Tests;

/// <summary>
/// A bucket in memory behind the interface of the AWS SDK, for the tests: it answers the three calls the storage makes the way S3 does (pages of a
/// listing, the size and the ETag of an object, a range of an object that is refused when the ETag is not the one asked for), and records them.
/// </summary>
public class FakeS3 : DispatchProxy
{
    private readonly Dictionary<string, (byte[] Data, string ETag)> _objects = new(StringComparer.Ordinal);

    /// <summary>How many objects a page of a listing has.</summary>
    public int PageSize { get; set; } = 1000;

    public List<string> Calls { get; } = [];

    /// <summary>The ranges that were asked for, with the ETag the request was tied to.</summary>
    public List<(string Key, long Start, long End, string? ETag)> Ranges { get; } = [];

    public string? Bucket { get; private set; }

    public static (IAmazonS3 Client, FakeS3 Fake) Create()
    {
        var client = Create<IAmazonS3, FakeS3>();
        return (client, (FakeS3)(object)client);
    }

    public FakeS3 Put(string key, byte[] data, string? etag = null)
    {
        _objects[key] = (data, etag ?? $"\"{key.Length}-{data.Length}-{Calls.Count}\"");
        return this;
    }

    /// <summary>Replaces an object by another version, as a writer does while a reader is reading.</summary>
    public void Replace(string key, byte[] data) => _objects[key] = (data, $"\"changed-{Guid.NewGuid():N}\"");

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);
        Calls.Add(targetMethod.Name);
        return targetMethod.Name switch
        {
            "ListObjectsV2Async" => Task.FromResult(List((ListObjectsV2Request)args[0]!)),
            "GetObjectMetadataAsync" => Task.FromResult(Metadata((GetObjectMetadataRequest)args[0]!)),
            "GetObjectAsync" => Task.FromResult(Get((GetObjectRequest)args[0]!)),
            _ => throw new NotSupportedException($"The test bucket does not do '{targetMethod.Name}'."),
        };
    }

    private ListObjectsV2Response List(ListObjectsV2Request request)
    {
        Bucket = request.BucketName;
        var matching = _objects.Where(pair => pair.Key.StartsWith(request.Prefix ?? "", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        int start = string.IsNullOrEmpty(request.ContinuationToken) ? 0 : int.Parse(request.ContinuationToken, System.Globalization.CultureInfo.InvariantCulture);
        var page = matching.Skip(start).Take(PageSize).ToList();
        bool more = start + page.Count < matching.Count;
        return new ListObjectsV2Response
        {
            S3Objects = page.Select(pair => new S3Object { Key = pair.Key, Size = pair.Value.Data.Length }).ToList(),
            IsTruncated = more,
            NextContinuationToken = more ? (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        };
    }

    private GetObjectMetadataResponse Metadata(GetObjectMetadataRequest request)
    {
        Bucket = request.BucketName;
        var (data, etag) = Find(request.Key);
        return new GetObjectMetadataResponse { ContentLength = data.Length, ETag = etag };
    }

    private GetObjectResponse Get(GetObjectRequest request)
    {
        Bucket = request.BucketName;
        var (data, etag) = Find(request.Key);
        long start = request.ByteRange?.Start ?? 0;
        long end = request.ByteRange?.End ?? data.Length - 1;
        Ranges.Add((request.Key, start, end, request.EtagToMatch));
        if (request.EtagToMatch is not null && request.EtagToMatch != etag)
        {
            throw new AmazonS3Exception("At least one of the pre-conditions you specified did not hold.", ErrorType.Sender, "PreconditionFailed", "request", HttpStatusCode.PreconditionFailed);
        }

        long last = Math.Min(end, data.Length - 1);
        return new GetObjectResponse { ResponseStream = new MemoryStream(data, (int)start, (int)(last - start + 1), writable: false) };
    }

    private (byte[] Data, string ETag) Find(string key) =>
        _objects.TryGetValue(key, out var found)
            ? found
            : throw new AmazonS3Exception("The specified key does not exist.", ErrorType.Sender, "NoSuchKey", "request", HttpStatusCode.NotFound);
}
