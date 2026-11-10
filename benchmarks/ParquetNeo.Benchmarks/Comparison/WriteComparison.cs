using BenchmarkDotNet.Attributes;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using PqNet = Parquet;
using PqNetSchema = Parquet.Schema;
using PqSharp = ParquetSharp;

namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>Writing the comparison dataset as one row group to a memory stream, with Snappy or without compression.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class WriteComparison
{
    private ComparisonData _data = null!;
    private MemoryStream _output = null!;
    private ParquetSchema _schema = null!;
    private ParquetWriterOptions _options = null!;
    private PqNetSchema.ParquetSchema _netSchema = null!;
    private PqNet.ParquetOptions _netOptions = null!;
    private PqNetSchema.DataField[] _netFields = null!;
    private ParquetWriterOptions _plainOptions = null!;
    private ParquetWriterOptions _adaptiveOptions = null!;
    private PqSharp.Column[] _sharpColumns = null!;

    [Params(CompressionCodec.Uncompressed, CompressionCodec.Snappy)]
    public CompressionCodec Compression { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new ComparisonData();
        _output = new MemoryStream(64 * 1024 * 1024);
        _schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("Customer", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
            new PrimitiveNode("Amount", PhysicalType.Double, FieldRepetitionType.Optional),
            new PrimitiveNode("Quantity", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("Shipped", PhysicalType.Boolean, FieldRepetitionType.Required),
        ]);
        _options = new ParquetWriterOptions { Compression = Compression, Codecs = new CodecRegistry().AddSnappy() };
        _netSchema = new PqNetSchema.ParquetSchema(
            new PqNetSchema.DataField<long>("Id"),
            new PqNetSchema.DataField<string?>("Customer"),
            new PqNetSchema.DataField<double?>("Amount"),
            new PqNetSchema.DataField<int>("Quantity"),
            new PqNetSchema.DataField<bool>("Shipped"));
        _netFields = _netSchema.GetDataFields();
        _plainOptions = _options with { DictionaryEnabled = false };
        _adaptiveOptions = _options with
        {
            ColumnDictionaries = new Dictionary<string, DictionarySettings> { ["Id"] = DictionaryMode.Adaptive, ["Amount"] = DictionaryMode.Adaptive },
        };
        _netOptions = new PqNet.ParquetOptions
        {
            CompressionMethod = Compression == CompressionCodec.Snappy ? PqNet.CompressionMethod.Snappy : PqNet.CompressionMethod.None,
            RowGroupSize = ComparisonData.Rows,
        };
        _sharpColumns = new PqSharp.Column[]
        {
            new PqSharp.Column<long>("Id"),
            new PqSharp.Column<string?>("Customer"),
            new PqSharp.Column<double?>("Amount"),
            new PqSharp.Column<int>("Quantity"),
            new PqSharp.Column<bool>("Shipped"),
        };
    }

    [GlobalCleanup]
    public void Cleanup() => _output.Dispose();

    [Benchmark(Baseline = true, Description = "ParquetNeo, columns")]
    public long ParquetNeoColumns()
    {
        _output.SetLength(0);
        using var writer = new ParquetFileWriter(_output, _schema, _options, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(_data.Ids);
            group.NextColumn().WriteStrings(_data.CustomerValues, _data.CustomerLevels);
            group.NextColumn().WriteBatch(_data.AmountValues, _data.AmountLevels);
            group.NextColumn().WriteBatch(_data.Quantities);
            group.NextColumn().WriteBatch(_data.Shipped);
        }

        writer.Close();
        return _output.Length;
    }

    /// <summary>The same columns encoded on four threads with <see cref="RowGroupWriter.WriteColumns(IReadOnlyList{Action{ColumnWriter}}, int, CancellationToken)"/>; the file is the same.</summary>
    [Benchmark(Description = "ParquetNeo, columns, 4 threads")]
    public long ParquetNeoColumnsInParallel()
    {
        _output.SetLength(0);
        using var writer = new ParquetFileWriter(_output, _schema, _options, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.WriteColumns(
                [
                    column => column.WriteBatch(_data.Ids),
                    column => column.WriteStrings(_data.CustomerValues, _data.CustomerLevels),
                    column => column.WriteBatch(_data.AmountValues, _data.AmountLevels),
                    column => column.WriteBatch(_data.Quantities),
                    column => column.WriteBatch(_data.Shipped),
                ],
                maxDegreeOfParallelism: 4);
        }

        writer.Close();
        return _output.Length;
    }

    /// <summary>
    /// Parquet.Net writes every column PLAIN, with no dictionary, which makes its files about twice the size of the others'; this
    /// is ParquetNeo doing the same work, for a like-for-like comparison with it (see the sizes with <c>-- --sizes</c>).
    /// </summary>
    [Benchmark(Description = "ParquetNeo, columns, no dictionary")]
    public long ParquetNeoColumnsPlain()
    {
        _output.SetLength(0);
        using var writer = new ParquetFileWriter(_output, _schema, _plainOptions, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(_data.Ids);
            group.NextColumn().WriteStrings(_data.CustomerValues, _data.CustomerLevels);
            group.NextColumn().WriteBatch(_data.AmountValues, _data.AmountLevels);
            group.NextColumn().WriteBatch(_data.Quantities);
            group.NextColumn().WriteBatch(_data.Shipped);
        }

        writer.Close();
        return _output.Length;
    }

    /// <summary>The columns of mostly different values (the identifier and the amount) drop their dictionary after the first values.</summary>
    [Benchmark(Description = "ParquetNeo, columns, adaptive dictionaries")]
    public long ParquetNeoColumnsAdaptive()
    {
        _output.SetLength(0);
        using var writer = new ParquetFileWriter(_output, _schema, _adaptiveOptions, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(_data.Ids);
            group.NextColumn().WriteStrings(_data.CustomerValues, _data.CustomerLevels);
            group.NextColumn().WriteBatch(_data.AmountValues, _data.AmountLevels);
            group.NextColumn().WriteBatch(_data.Quantities);
            group.NextColumn().WriteBatch(_data.Shipped);
        }

        writer.Close();
        return _output.Length;
    }

    [Benchmark(Description = "ParquetNeo, serializer")]
    public long ParquetNeoSerializer()
    {
        _output.SetLength(0);
        ParquetSerializer.Serialize(_data.Orders, _output, new ParquetSerializerOptions { RowGroupSize = ComparisonData.Rows, Writer = _options });
        return _output.Length;
    }

    [Benchmark(Description = "ParquetNeo, serializer, 4 threads")]
    public long ParquetNeoSerializerInParallel()
    {
        _output.SetLength(0);
        ParquetSerializer.Serialize(_data.Orders, _output, new ParquetSerializerOptions { RowGroupSize = ComparisonData.Rows, Writer = _options, MaxDegreeOfParallelism = 4 });
        return _output.Length;
    }

    [Benchmark(Description = "Parquet.Net, columns")]
    public async Task<long> ParquetNetColumns()
    {
        _output.SetLength(0);
        var fields = _netFields;
        await using (var writer = await PqNet.ParquetWriter.CreateAsync(_netSchema, _output, _netOptions, append: false))
        {
            using var group = writer.CreateRowGroup();
            await group.WriteAsync<long>(fields[0], _data.Ids);
            await group.WriteAsync(fields[1], _data.Customers);
            await group.WriteAsync<double>(fields[2], _data.Amounts);
            await group.WriteAsync<int>(fields[3], _data.Quantities);
            await group.WriteAsync<bool>(fields[4], _data.Shipped);
        }

        return _output.Length;
    }

    [Benchmark(Description = "Parquet.Net, serializer")]
    public async Task<long> ParquetNetSerializer()
    {
        _output.SetLength(0);
        await PqNet.Serialization.ParquetSerializer.SerializeAsync(_data.Orders, _output, _netOptions);
        return _output.Length;
    }

    [Benchmark(Description = "ParquetSharp, columns")]
    public long ParquetSharpColumns()
    {
        _output.SetLength(0);
        using (var writer = new PqSharp.ParquetFileWriter(_output, _sharpColumns, logicalTypeFactory: null, Compression == CompressionCodec.Snappy ? PqSharp.Compression.Snappy : PqSharp.Compression.Uncompressed, keyValueMetadata: null, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            using (var column = group.NextColumn().LogicalWriter<long>())
            {
                column.WriteBatch(_data.Ids);
            }

            using (var column = group.NextColumn().LogicalWriter<string?>())
            {
                column.WriteBatch(_data.Customers);
            }

            using (var column = group.NextColumn().LogicalWriter<double?>())
            {
                column.WriteBatch(_data.Amounts);
            }

            using (var column = group.NextColumn().LogicalWriter<int>())
            {
                column.WriteBatch(_data.Quantities);
            }

            using (var column = group.NextColumn().LogicalWriter<bool>())
            {
                column.WriteBatch(_data.Shipped);
            }

            writer.Close();
        }

        return _output.Length;
    }
}
