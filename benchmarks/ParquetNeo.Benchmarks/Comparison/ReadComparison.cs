using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization;
using PqNet = Parquet;
using PqSharp = ParquetSharp;

namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>
/// Reading the comparison dataset back into arrays (or objects, for the serializers). Every library reads the same file, written
/// by ParquetSharp, which wraps the C++ implementation: one row group, dictionary encoded where it chooses to, Snappy or not.
/// </summary>
/// <remarks>
/// Each library is driven by the fastest way it has to get arrays of values and, for a nullable column, which values are there:
/// ParquetNeo reads the values with their definition levels; Parquet.Net's <c>ReadRawAsync</c> does the same for the nullable
/// double (its <c>ReadAsync</c> expands it to <c>double?</c>, which takes about a quarter longer); ParquetSharp reads from a
/// native buffer (no call back into managed code for each read) and its physical column readers for the numbers, which are
/// quicker than its logical readers and allocate a fraction of the memory. Text goes through the logical readers of ParquetSharp
/// and <c>ReadAsync</c> of Parquet.Net, the only ways they have to make strings. The arrays are allocated by each call, for all.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class ReadComparison
{
    private byte[] _file = [];
    private ParquetReaderOptions _options = null!;
    private PqNet.ParquetOptions _netOptions = null!;
    private GCHandle _pinnedFile;

    [Params(CompressionCodec.Uncompressed, CompressionCodec.Snappy)]
    public CompressionCodec Compression { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = new ComparisonData();
        var stream = new MemoryStream();
        var columns = new PqSharp.Column[]
        {
            new PqSharp.Column<long>("Id"),
            new PqSharp.Column<string?>("Customer"),
            new PqSharp.Column<double?>("Amount"),
            new PqSharp.Column<int>("Quantity"),
            new PqSharp.Column<bool>("Shipped"),
        };
        using (var writer = new PqSharp.ParquetFileWriter(stream, columns, logicalTypeFactory: null, Compression == CompressionCodec.Snappy ? PqSharp.Compression.Snappy : PqSharp.Compression.Uncompressed, keyValueMetadata: null, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            using (var column = group.NextColumn().LogicalWriter<long>())
            {
                column.WriteBatch(data.Ids);
            }

            using (var column = group.NextColumn().LogicalWriter<string?>())
            {
                column.WriteBatch(data.Customers);
            }

            using (var column = group.NextColumn().LogicalWriter<double?>())
            {
                column.WriteBatch(data.Amounts);
            }

            using (var column = group.NextColumn().LogicalWriter<int>())
            {
                column.WriteBatch(data.Quantities);
            }

            using (var column = group.NextColumn().LogicalWriter<bool>())
            {
                column.WriteBatch(data.Shipped);
            }

            writer.Close();
        }

        _file = stream.ToArray();
        _pinnedFile = GCHandle.Alloc(_file, GCHandleType.Pinned);
        _options = new ParquetReaderOptions { Codecs = new CodecRegistry().AddSnappy() };
        _netOptions = new PqNet.ParquetOptions();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_pinnedFile.IsAllocated)
        {
            _pinnedFile.Free();
        }
    }

    [Benchmark(Baseline = true, Description = "ParquetNeo, columns")]
    public long ParquetNeoColumns()
    {
        const int Rows = ComparisonData.Rows;
        using var file = new ParquetFileReader(new MemoryStream(_file), _options);
        var group = file.OpenRowGroup(0);
        var ids = new long[Rows];
        var customers = new string[Rows];
        var amounts = new double[Rows];
        var quantities = new int[Rows];
        var shipped = new bool[Rows];
        var levels = new int[Rows];
        long entries = 0;
        using (var column = group.OpenColumn(0))
        {
            entries += column.ReadBatch(ids).Entries;
        }

        using (var column = group.OpenColumn(1))
        {
            entries += ReadAll(column, customers, levels);
        }

        using (var column = group.OpenColumn(2))
        {
            entries += column.ReadBatch(amounts, levels).Entries;
        }

        using (var column = group.OpenColumn(3))
        {
            entries += column.ReadBatch(quantities).Entries;
        }

        using (var column = group.OpenColumn(4))
        {
            entries += column.ReadBatch(shipped).Entries;
        }

        return entries;
    }

    /// <summary>Strings come in batches that end at the end of a page, so keep asking until the column is read.</summary>
    private static long ReadAll(ColumnReader column, string[] values, int[] levels)
    {
        long entries = 0;
        int present = 0;
        while (entries < values.Length)
        {
            var read = column.ReadStrings(values.AsSpan(present, values.Length - present), levels.AsSpan((int)entries, values.Length - (int)entries));
            if (read.Entries == 0)
            {
                break;
            }

            entries += read.Entries;
            present += read.Values;
        }

        return entries;
    }

    [Benchmark(Description = "ParquetNeo, serializer")]
    public int ParquetNeoSerializer() => ParquetSerializer.Deserialize<Order>(new MemoryStream(_file), new ParquetDeserializerOptions { Reader = _options }).Count;

    [Benchmark(Description = "Parquet.Net, columns")]
    public async Task<long> ParquetNetColumns()
    {
        const int Rows = ComparisonData.Rows;
        await using var reader = await PqNet.ParquetReader.CreateAsync(new MemoryStream(_file), _netOptions);
        using var group = reader.OpenRowGroupReader(0);
        var fields = reader.Schema.GetDataFields();
        var ids = new long[Rows];
        var customers = new string?[Rows];
        var amounts = new double[Rows];
        var definitionLevels = new int[Rows];
        var quantities = new int[Rows];
        var shipped = new bool[Rows];
        await group.ReadAsync<long>(fields[0], ids);
        await group.ReadAsync(fields[1], customers.AsMemory()!);
        await group.ReadRawAsync<double>(fields[2], amounts, definitionLevels, null);
        await group.ReadAsync<int>(fields[3], quantities);
        await group.ReadAsync<bool>(fields[4], shipped);
        return ids.Length + customers.Length + amounts.Length + quantities.Length + shipped.Length;
    }

    [Benchmark(Description = "Parquet.Net, serializer")]
    public async Task<int> ParquetNetSerializer()
    {
        var result = await PqNet.Serialization.ParquetSerializer.DeserializeAsync<Order>(new MemoryStream(_file), _netOptions);
        return result.Data.Count;
    }

    [Benchmark(Description = "ParquetSharp, columns")]
    public long ParquetSharpColumns()
    {
        const int Rows = ComparisonData.Rows;
        using var buffer = new PqSharp.IO.Buffer(_pinnedFile.AddrOfPinnedObject(), _file.Length);
        using var input = new PqSharp.IO.BufferReader(buffer);
        using var file = new PqSharp.ParquetFileReader(input);
        using var group = file.RowGroup(0);
        var ids = new long[Rows];
        var amounts = new double[Rows];
        var definitionLevels = new short[Rows];
        var quantities = new int[Rows];
        var shipped = new bool[Rows];
        long entries = 0;
        using (var column = (PqSharp.ColumnReader<long>)group.Column(0))
        {
            entries += ReadAll(column, ids);
        }

        using (var column = group.Column(1).LogicalReader<string?>())
        {
            entries += column.ReadAll(Rows).Length;
        }

        using (var column = (PqSharp.ColumnReader<double>)group.Column(2))
        {
            // A batch may be shorter than asked for: keep asking until every row is read.
            long levels = 0;
            long values = 0;
            while (levels < Rows)
            {
                long read = column.ReadBatch(Rows - levels, definitionLevels.AsSpan((int)levels), Span<short>.Empty, amounts.AsSpan((int)values), out long present);
                if (read == 0)
                {
                    break;
                }

                levels += read;
                values += present;
            }

            entries += levels;
        }

        using (var column = (PqSharp.ColumnReader<int>)group.Column(3))
        {
            entries += ReadAll(column, quantities);
        }

        using (var column = (PqSharp.ColumnReader<bool>)group.Column(4))
        {
            entries += ReadAll(column, shipped);
        }

        return entries;
    }

    /// <summary>Reads a column of required values; a batch may be shorter than asked for, so it asks until every row is read.</summary>
    private static long ReadAll<T>(PqSharp.ColumnReader<T> column, T[] destination)
        where T : unmanaged
    {
        long done = 0;
        while (done < destination.Length)
        {
            column.ReadBatch(destination.Length - done, destination.AsSpan((int)done), out long read);
            if (read == 0)
            {
                break;
            }

            done += read;
        }

        return done;
    }
}
