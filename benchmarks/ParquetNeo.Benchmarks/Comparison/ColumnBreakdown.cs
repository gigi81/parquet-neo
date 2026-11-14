using BenchmarkDotNet.Attributes;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Benchmarks.Comparison;

/// <summary>Where the time of the low-level writer and reader goes: each column of the comparison dataset on its own, uncompressed.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class ColumnBreakdown
{
    private ComparisonData _data = null!;
    private MemoryStream _output = null!;
    private ParquetSchema[] _schemas = null!;
    private byte[][] _files = null!;
    private long[] _ids = new long[ComparisonData.Rows];
    private string[] _customers = new string[ComparisonData.Rows];
    private double[] _amounts = new double[ComparisonData.Rows];
    private int[] _quantities = new int[ComparisonData.Rows];
    private bool[] _shipped = new bool[ComparisonData.Rows];
    private int[] _levels = new int[ComparisonData.Rows];

    [GlobalSetup]
    public void Setup()
    {
        _data = new ComparisonData();
        _output = new MemoryStream(64 * 1024 * 1024);
        _schemas =
        [
            new ParquetSchema([new PrimitiveNode("Id", PhysicalType.Int64, FieldRepetitionType.Required)]),
            new ParquetSchema([new PrimitiveNode("Customer", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String)]),
            new ParquetSchema([new PrimitiveNode("Amount", PhysicalType.Double, FieldRepetitionType.Optional)]),
            new ParquetSchema([new PrimitiveNode("Quantity", PhysicalType.Int32, FieldRepetitionType.Required)]),
            new ParquetSchema([new PrimitiveNode("Shipped", PhysicalType.Boolean, FieldRepetitionType.Required)]),
        ];
        _files = new byte[5][];
        for (int i = 0; i < 5; i++)
        {
            WriteColumn(i);
            _files[i] = _output.ToArray();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _output.Dispose();

    private void WriteColumn(int column)
    {
        _output.SetLength(0);
        using var writer = new ParquetFileWriter(_output, _schemas[column], new ParquetWriterOptions(), leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            var w = group.NextColumn();
            switch (column)
            {
                case 0: w.WriteBatch(_data.Ids); break;
                case 1: w.WriteStrings(_data.CustomerValues, _data.CustomerLevels); break;
                case 2: w.WriteBatch(_data.AmountValues, _data.AmountLevels); break;
                case 3: w.WriteBatch(_data.Quantities); break;
                default: w.WriteBatch(_data.Shipped); break;
            }
        }

        writer.Close();
    }

    [Benchmark] public long WriteId() { WriteColumn(0); return _output.Length; }

    [Benchmark] public long WriteCustomer() { WriteColumn(1); return _output.Length; }

    [Benchmark] public long WriteAmount() { WriteColumn(2); return _output.Length; }

    [Benchmark] public long WriteQuantity() { WriteColumn(3); return _output.Length; }

    [Benchmark] public long WriteShipped() { WriteColumn(4); return _output.Length; }

    private int ReadColumn(int column)
    {
        using var file = new ParquetFileReader(new MemoryStream(_files[column]));
        using var reader = file.OpenRowGroup(0).OpenColumn(0);
        switch (column)
        {
            case 0: return reader.ReadBatch(_ids).Entries;
            case 1:
                int entries = 0;
                int present = 0;
                while (entries < _customers.Length)
                {
                    var read = reader.ReadStrings(_customers.AsSpan(present), _levels.AsSpan(entries));
                    if (read.Entries == 0)
                    {
                        break;
                    }

                    entries += read.Entries;
                    present += read.Values;
                }

                return entries;
            case 2: return reader.ReadBatch(_amounts, _levels).Entries;
            case 3: return reader.ReadBatch(_quantities).Entries;
            default: return reader.ReadBatch(_shipped).Entries;
        }
    }

    [Benchmark] public int ReadId() => ReadColumn(0);

    [Benchmark] public int ReadCustomer() => ReadColumn(1);

    [Benchmark] public int ReadAmount() => ReadColumn(2);

    [Benchmark] public int ReadQuantity() => ReadColumn(3);

    [Benchmark] public int ReadShipped() => ReadColumn(4);
}
