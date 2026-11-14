# Reading from Amazon S3 and Azure Blob Storage

Parquet files in an object storage are read without downloading them: a file is asked for its footer, and then for the byte ranges of the
columns and pages that a query needs. A filter that rules out a row group or a page (see [Skipping what is not needed](filtering.md))
means that its bytes are never requested.

| Package | Storage | Class |
|---|---|---|
| `ParquetNeo.Storage.S3` | Amazon S3, and S3 compatible storage such as MinIO | `S3ParquetStorage` |
| `ParquetNeo.Storage.AzureBlobs` | Azure Blob Storage, and the Azurite emulator | `AzureBlobParquetStorage` |

Both are in `ParquetNeo.All`. They implement `IParquetStorage` (package `ParquetNeo`), so code that takes one works with either, or with a
folder of a disk (`LocalFileStorage`).

## Amazon S3

```csharp
using Amazon.S3;
using ParquetNeo.Storage.S3;

var client = new AmazonS3Client();                       // logs in as the AWS SDK does: environment, profile, role
var storage = new S3ParquetStorage(client, "my-bucket");
```

The permissions needed are `s3:ListBucket` (to list files) and `s3:GetObject`. For MinIO or another compatible storage, create the
client with its endpoint (`new AmazonS3Config { ServiceURL = "http://localhost:9000", ForcePathStyle = true }`).

## Azure Blob Storage

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using ParquetNeo.Storage.AzureBlobs;

var container = new BlobContainerClient(new Uri("https://myaccount.blob.core.windows.net/data"), new DefaultAzureCredential());
var storage = new AzureBlobParquetStorage(container);
```

How to log in (a connection string, a key, Microsoft Entra ID) is up to the container client you give. With Entra ID the role
`Storage Blob Data Reader` is enough. (`Azure.Identity` is a package of its own.)

## Reading one file

A file of a storage opens as a seekable stream that asks the storage for ranges of the file as it is read. Give it to the
asynchronous methods of the serializer, which read only the footer and the column chunks of the row groups that a filter keeps:

```csharp
await using var stream = await storage.OpenReadAsync("people/2024/part-0.parquet");

var rows = await ParquetSerializer.DeserializeAsync<Person>(stream, new ParquetDeserializerOptions
{
    RowGroupFilter = ParquetFilter.Column("Id").GreaterThanOrEqual(1_000_000),
});
```

The stream is also a valid input of `ParquetFileReader` (the low-level API), where you choose the row groups and the columns yourself.
Its synchronous reads wait for the storage, so prefer the asynchronous methods in a server.

## Reading a dataset

`ParquetDataset.Open` takes a storage and the start of the keys of the files, and queries them like a directory (see [LINQ over files](linq.md)):

```csharp
using ParquetNeo.Linq;

var people = ParquetDataset.Open<Person>(storage, "people/2024/");

var recent = people.Where(p => p.Id >= 1_000_000 && p.Id < 1_000_100)
                   .Select(p => new { p.Id, p.Name })
                   .ToList();

var plan = people.Where(p => p.Id >= 1_000_000).Explain();   // how many files and row groups are skipped
```

The files are the objects whose key starts with the prefix and matches `SearchPattern` (`*.parquet`), with the ones in folders below it
unless `Recursive` is `false`. Files are read in parallel, as for a directory. A file that a filter rules out costs a request for its
footer, and no more.

## How it reads

- **The end of a file first.** A Parquet reader finds the footer from the last bytes of a file. The first read there takes the last 64 KiB in
  one request and keeps them, which usually holds the whole footer.
- **Blocks for small reads.** A read of less than 1 MiB takes the block it is in and keeps the last few, so that the many small reads of
  page headers are not a request each. A larger read, such as a whole column chunk, is one request for exactly what it asks.
- **The same version of the file.** Every request is tied to the ETag the file had when it was opened. A file that is replaced while it is read gives
  an `IOException`, and never a result made of parts of two versions.

All three are set with `RangeReadStreamOptions` (`TailSize`, `BlockSize`, `CachedBlocks`), which the storage classes take as their last argument.
Make the blocks bigger for files read in long runs, and the tail bigger for files with a big footer (thousands of row groups and columns).

## Things to keep in mind

- **Requests cost time and money.** A file is read with a handful of requests, plus one per column chunk of a row group that is read. Row
  groups of a few hundred thousand rows or more, and columns that a query leaves out, make a read cheaper.
- **Listing is by prefix.** There are no real folders: `people/2024/` is a prefix. A very large prefix takes many pages of a listing.
- **Write a page index** (`WritePageIndex`) and, for columns that are searched by value, bloom filters: they cut what is fetched
  most in a storage. They are written when the files are made.
- **Not here:** writing to a storage. A dataset waits for the storage on the threads that read its files (it reads several files in parallel), so
  for a server that must not block threads, read single files with the asynchronous methods.
