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
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class ReadComparison
{
    private byte[] _file = [];
    private ParquetReaderOptions _options = null!;
    private PqNet.ParquetOptions _netOptions = null!;

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
        _options = new ParquetReaderOptions { Codecs = new CodecRegistry().AddSnappy() };
        _netOptions = new PqNet.ParquetOptions();
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
        var amounts = new double?[Rows];
        var quantities = new int[Rows];
        var shipped = new bool[Rows];
        await group.ReadAsync<long>(fields[0], ids);
        await group.ReadAsync(fields[1], customers.AsMemory()!);
        await group.ReadAsync<double>(fields[2], amounts);
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
        using var file = new PqSharp.ParquetFileReader(new MemoryStream(_file));
        using var group = file.RowGroup(0);
        long rows = 0;
        using (var column = group.Column(0).LogicalReader<long>())
        {
            rows += column.ReadAll(ComparisonData.Rows).Length;
        }

        using (var column = group.Column(1).LogicalReader<string?>())
        {
            rows += column.ReadAll(ComparisonData.Rows).Length;
        }

        using (var column = group.Column(2).LogicalReader<double?>())
        {
            rows += column.ReadAll(ComparisonData.Rows).Length;
        }

        using (var column = group.Column(3).LogicalReader<int>())
        {
            rows += column.ReadAll(ComparisonData.Rows).Length;
        }

        using (var column = group.Column(4).LogicalReader<bool>())
        {
            rows += column.ReadAll(ComparisonData.Rows).Length;
        }

        return rows;
    }
}
