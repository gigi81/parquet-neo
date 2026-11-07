using BenchmarkDotNet.Attributes;
using ParquetNeo.Compression;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Benchmarks;

/// <summary>Writing 1M rows of three columns (a required long, a nullable double, a nullable string) to a discarding stream.</summary>
[MemoryDiagnoser]
public class WriterBenchmarks
{
    private const int Rows = 1_000_000;
    private ParquetSchema _schema = null!;
    private long[] _ids = [];
    private double[] _scores = [];
    private string[] _names = [];
    private int[] _scoreLevels = [];
    private int[] _nameLevels = [];
    private ParquetWriterOptions _options = null!;

    [Params(CompressionCodec.Uncompressed, CompressionCodec.Snappy)]
    public CompressionCodec Compression { get; set; }

    [Params(false, true)]
    public bool Dictionary { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _schema = new ParquetSchema(
        [
            new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Optional),
            new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        ]);
        _ids = Enumerable.Range(0, Rows).Select(i => (long)i).ToArray();
        _scoreLevels = Enumerable.Range(0, Rows).Select(i => i % 3 == 0 ? 0 : 1).ToArray();
        _scores = Enumerable.Range(0, Rows).Where(i => i % 3 != 0).Select(i => i * 0.25).ToArray();
        _nameLevels = Enumerable.Range(0, Rows).Select(i => i % 10 == 0 ? 0 : 1).ToArray();
        _names = Enumerable.Range(0, Rows).Where(i => i % 10 != 0).Select(i => "customer-" + (i % 5000)).ToArray();
        _options = new ParquetWriterOptions { Compression = Compression, Codecs = new CodecRegistry().AddSnappy(), DictionaryEnabled = Dictionary };
    }

    [Benchmark]
    public long WriteFile()
    {
        using var writer = new ParquetFileWriter(Stream.Null, _schema, _options, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(_ids);
            group.NextColumn().WriteBatch(_scores, _scoreLevels);
            group.NextColumn().WriteStrings(_names, _nameLevels);
        }

        writer.Close();
        return writer.Position;
    }
}
