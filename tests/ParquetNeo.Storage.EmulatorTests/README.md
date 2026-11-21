# Storage emulator tests

The unit tests of `ParquetNeo.Storage.S3` and `ParquetNeo.Storage.AzureBlobs` use fakes of the SDKs, which only imitate how a server answers. These tests run the
same storages against **real servers**: an S3 server (MinIO in the workflow, moto too) and **Azurite**, the emulator of Azure Blob Storage. They check what a fake
cannot: how a server answers a request for a range (the first byte, the last, past the end), a listing that goes over more than one page (1 150 objects against the
1 000 of S3), a tag of version that no longer matches (the object changed while it was read), an object that is not there, a cancelled request; and that a Parquet
file, and a dataset of files, are read through the storage and read little of it when a filter rules out row groups and files.

Every scenario runs against both backends (`s3` and `azure`).

## Running them

The tests of a backend are skipped unless its server is set in the environment. `PARQUETNEO_EMULATOR_TESTS` (`s3`, `azure`, or both separated by a comma) says which
ones **must** be there, and then a missing server is a failure: the workflow sets it.

| Backend | Variables |
|---|---|
| `s3` | `PARQUETNEO_S3_ENDPOINT` (for example `http://127.0.0.1:9000`), `PARQUETNEO_S3_ACCESS_KEY` and `PARQUETNEO_S3_SECRET_KEY` (default `minioadmin`) |
| `azure` | `PARQUETNEO_AZURE_CONNECTION_STRING` (`UseDevelopmentStorage=true` for Azurite on its default ports) |

With Docker:

```bash
docker run -d -p 9000:9000 -e MINIO_ROOT_USER=minioadmin -e MINIO_ROOT_PASSWORD=minioadmin minio/minio:RELEASE.2025-04-22T22-12-26Z server /data
docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck
export PARQUETNEO_EMULATOR_TESTS=s3,azure PARQUETNEO_S3_ENDPOINT=http://127.0.0.1:9000 PARQUETNEO_AZURE_CONNECTION_STRING=UseDevelopmentStorage=true
dotnet test --project tests/ParquetNeo.Storage.EmulatorTests -c Release
```

Without Docker, `npm install --global azurite` and `pip install "moto[server]"` give the same servers (`azurite-blob --skipApiVersionCheck`, `moto_server -p 9000`; moto
takes any credentials).

Every run makes a bucket and a container of its own (`parquetneo-` and a random name) and leaves them: the servers are meant to be thrown away.

`--skipApiVersionCheck` is needed because the version of the API that the SDK asks for can be newer than the one the emulator knows.

## The workflow

`.github/workflows/storage-emulators.yml` runs on pushes to `main`, by hand, and on pull requests that change the storages or these tests. It has a job for S3 with two servers
(MinIO and moto, which are different code answering the same protocol) and a job for Azure with Azurite.
