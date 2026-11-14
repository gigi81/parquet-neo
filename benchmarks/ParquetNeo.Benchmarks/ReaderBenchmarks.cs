using BenchmarkDotNet.Attributes;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Benchmarks;

/// <summary>Reading back the file that <see cref="WriterBenchmarks"/> writes: 1M rows of a required long, a nullable double and a nullable string.</summary>
[MemoryDiagnoser]
public class ReaderBenchmarks
{
    private const int Rows = 1_000_000;
    private const int BatchSize = 8192;
    private byte[] _file = [];
    private ParquetReaderOptions _options = null!;
    private readonly long[] _ids = new long[BatchSize];
    private readonly double[] _scores = new double[BatchSize];
    private readonly ReadOnlyMemory<byte>[] _names = new ReadOnlyMemory<byte>[BatchSize];
    private readonly int[] _levels = new int[BatchSize];

    [Params(CompressionCodec.Uncompressed, CompressionCodec.Snappy)]
    public CompressionCodec Compression { get; set; }

    [Params(false, true)]
    public bool Dictionary { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var registry = new CodecRegistry().AddSnappy();
        _options = new ParquetReaderOptions { Codecs = registry };
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Optional),
            new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        ]);
        var ids = Enumerable.Range(0, Rows).Select(i => (long)i).ToArray();
        var scoreLevels = Enumerable.Range(0, Rows).Select(i => i % 3 == 0 ? 0 : 1).ToArray();
        var scores = Enumerable.Range(0, Rows).Where(i => i % 3 != 0).Select(i => i * 0.25).ToArray();
        var nameLevels = Enumerable.Range(0, Rows).Select(i => i % 10 == 0 ? 0 : 1).ToArray();
        var names = Enumerable.Range(0, Rows).Where(i => i % 10 != 0).Select(i => "customer-" + (i % 5000)).ToArray();

        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { Compression = Compression, Codecs = registry, DictionaryEnabled = Dictionary }, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(ids);
            group.NextColumn().WriteBatch(scores, scoreLevels);
            group.NextColumn().WriteStrings(names, nameLevels);
        }

        _file = stream.ToArray();
    }

    [Benchmark]
    public long ReadFile()
    {
        using var reader = new ParquetFileReader(new MemoryStream(_file), _options);
        var group = reader.OpenRowGroup(0);
        long total = 0;

        using (var ids = group.OpenColumn(0))
        {
            BatchRead read;
            while ((read = ids.ReadBatch(_ids)).Entries > 0)
            {
                total += _ids[read.Entries - 1];
            }
        }

        using (var scores = group.OpenColumn(1))
        {
            BatchRead read;
            while ((read = scores.ReadBatch(_scores, _levels)).Entries > 0)
            {
                total += read.Values;
            }
        }

        using (var names = group.OpenColumn(2))
        {
            BatchRead read;
            while ((read = names.ReadByteArrays(_names, _levels)).Entries > 0)
            {
                total += read.Values;
            }
        }

        return total;
    }
}
