using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using ParquetNeo.Storage;
using ParquetNeo.Storage.AzureBlobs;
using ParquetNeo.Storage.S3;

namespace ParquetNeo.Storage.EmulatorTests;

/// <summary>
/// An object storage the tests run against: a real server that speaks the protocol of S3 (SeaweedFS, moto, MinIO or any other) or of Azure Blob Storage (Azurite),
/// told by environment variables. A backend that is not set is skipped, unless <c>PARQUETNEO_EMULATOR_TESTS</c> lists it (<c>s3</c>, <c>azure</c>, or both
/// separated by commas), as the workflow does, and then a missing or unreachable one is a failure.
/// </summary>
internal abstract class Backend
{
    public const string RequiredVariable = "PARQUETNEO_EMULATOR_TESTS";

    private static readonly ConcurrentDictionary<string, Lazy<Task<Backend>>> Started = new();

    /// <summary>The names of the backends, which the tests take as an argument.</summary>
    public static IEnumerable<string> Names() => ["s3", "azure"];

    /// <summary>The backend of a name, made once for the whole run; skips the test when it is not set up.</summary>
    public static async Task<Backend> OpenAsync(string name)
    {
        var configured = name switch
        {
            "s3" => Environment.GetEnvironmentVariable("PARQUETNEO_S3_ENDPOINT") is { Length: > 0 },
            "azure" => Environment.GetEnvironmentVariable("PARQUETNEO_AZURE_CONNECTION_STRING") is { Length: > 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "There is no such backend."),
        };
        bool required = (Environment.GetEnvironmentVariable(RequiredVariable) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries).Contains(name);
        if (!configured)
        {
            if (required)
            {
                throw new InvalidOperationException($"The tests of '{name}' are required ({RequiredVariable}), but its server is not set: see the README of the project.");
            }

            Skip.Test($"No server for '{name}': see the README of tests/ParquetNeo.Storage.EmulatorTests.");
        }

        return await Started.GetOrAdd(name, key => new Lazy<Task<Backend>>(() => key == "s3" ? S3Backend.CreateAsync() : AzureBackend.CreateAsync())).Value.ConfigureAwait(false);
    }

    /// <summary>A prefix that no other test has used, so that tests do not see each other's objects.</summary>
    public static string NewPrefix(string name) => $"{name}-{Guid.NewGuid():N}/";

    public abstract string Name { get; }

    public abstract IParquetStorage CreateStorage(RangeReadStreamOptions? options = null);

    public abstract Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default);

    private sealed class S3Backend(IAmazonS3 client, string bucket) : Backend
    {
        public override string Name => "s3";

        public static async Task<Backend> CreateAsync()
        {
            var config = new AmazonS3Config
            {
                ServiceURL = Environment.GetEnvironmentVariable("PARQUETNEO_S3_ENDPOINT"),
                ForcePathStyle = true, // the emulators are addressed by path, not by a name of the bucket in the host
                AuthenticationRegion = "us-east-1",
            };
            var credentials = new BasicAWSCredentials(
                Environment.GetEnvironmentVariable("PARQUETNEO_S3_ACCESS_KEY") is { Length: > 0 } access ? access : "minioadmin",
                Environment.GetEnvironmentVariable("PARQUETNEO_S3_SECRET_KEY") is { Length: > 0 } secret ? secret : "minioadmin");
            var client = new AmazonS3Client(credentials, config);
            string bucket = "parquetneo-" + Guid.NewGuid().ToString("N")[..16];
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
            return new S3Backend(client, bucket);
        }

        public override IParquetStorage CreateStorage(RangeReadStreamOptions? options = null) => new S3ParquetStorage(client, bucket, options);

        public override Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default) =>
            client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(bytes) }, cancellationToken);
    }

    private sealed class AzureBackend(BlobContainerClient container) : Backend
    {
        public override string Name => "azure";

        public static async Task<Backend> CreateAsync()
        {
            var service = new BlobServiceClient(Environment.GetEnvironmentVariable("PARQUETNEO_AZURE_CONNECTION_STRING"));
            var container = service.GetBlobContainerClient("parquetneo-" + Guid.NewGuid().ToString("N")[..16]);
            await container.CreateAsync().ConfigureAwait(false);
            return new AzureBackend(container);
        }

        public override IParquetStorage CreateStorage(RangeReadStreamOptions? options = null) => new AzureBlobParquetStorage(container, options);

        public override async Task PutAsync(string key, byte[] bytes, CancellationToken cancellationToken = default) =>
            await container.GetBlobClient(key).UploadAsync(new BinaryData(bytes), overwrite: true, cancellationToken).ConfigureAwait(false);
    }
}
