using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ParquetNeo.Storage.AzureBlobs.Tests;

/// <summary>
/// A container in memory behind the classes of the Azure SDK, for the tests: it answers the calls the storage makes the way Blob Storage does (pages of a
/// listing, the size and the ETag of a blob, a range of a blob that is refused when the ETag is not the one asked for), and records them.
/// </summary>
public sealed class FakeContainer : BlobContainerClient
{
    private readonly Dictionary<string, (byte[] Data, ETag ETag)> _blobs = new(StringComparer.Ordinal);

    public int PageSize { get; set; } = 1000;

    /// <summary>The ranges that were asked for, with the ETag the request was tied to.</summary>
    public List<(string Key, long Offset, long Length, ETag? IfMatch)> Ranges { get; } = [];

    public int Listings { get; private set; }

    public int PropertyCalls { get; private set; }

    public string? LastPrefix { get; private set; }

    public override string Name => "fake-container";

    public FakeContainer Put(string key, byte[] data, string? etag = null)
    {
        _blobs[key] = (data, new ETag(etag ?? $"\"{key.Length}-{data.Length}\""));
        return this;
    }

    public void Replace(string key, byte[] data) => _blobs[key] = (data, new ETag($"\"changed-{Guid.NewGuid():N}\""));

    public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits = BlobTraits.None, BlobStates states = BlobStates.None, string? prefix = null, CancellationToken cancellationToken = default)
    {
        Listings++;
        LastPrefix = prefix;
        var matching = _blobs.Where(pair => pair.Key.StartsWith(prefix ?? "", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        var pages = new List<Page<BlobItem>>();
        for (int start = 0; start < Math.Max(matching.Count, 1); start += PageSize)
        {
            var items = matching.Skip(start).Take(PageSize)
                .Select(pair => BlobsModelFactory.BlobItem(name: pair.Key, properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, contentLength: pair.Value.Data.Length)))
                .ToList();
            pages.Add(Page<BlobItem>.FromValues(items, start + PageSize < matching.Count ? "next" : null, new FakeResponse(200)));
        }

        return AsyncPageable<BlobItem>.FromPages(pages);
    }

    public override BlobClient GetBlobClient(string blobName) => new FakeBlob(this, blobName);

    private sealed class FakeBlob(FakeContainer container, string key) : BlobClient
    {
        public override string Name => key;

        public override string BlobContainerName => container.Name;

        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            container.PropertyCalls++;
            var (data, etag) = Find();
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: data.Length, eTag: etag), new FakeResponse(200)));
        }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (data, etag) = Find();
            long offset = options?.Range.Offset ?? 0;
            long length = options?.Range.Length ?? data.Length - offset;
            container.Ranges.Add((key, offset, length, options?.Conditions?.IfMatch));
            if (options?.Conditions?.IfMatch is { } ifMatch && ifMatch != etag)
            {
                throw new RequestFailedException(412, "The condition specified using HTTP conditional header(s) is not met.", "ConditionNotMet", null);
            }

            length = Math.Min(length, data.Length - offset);
            var content = new MemoryStream(data, (int)offset, (int)length, writable: false);
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(content, BlobsModelFactory.BlobDownloadDetails()), new FakeResponse(206)));
        }

        private (byte[] Data, ETag ETag) Find() =>
            container._blobs.TryGetValue(key, out var found)
                ? found
                : throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
    }
}
