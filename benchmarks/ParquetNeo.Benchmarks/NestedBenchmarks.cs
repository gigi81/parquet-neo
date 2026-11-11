using BenchmarkDotNet.Attributes;
using ParquetNeo.Core;
using ParquetNeo.Nested;
using ParquetNeo.Schema;

namespace ParquetNeo.Benchmarks;

/// <summary>Writing and reading one nullable list of nullable ints: 1M rows of 0 to 4 elements, to a discarding stream and from memory.</summary>
[MemoryDiagnoser]
public class NestedBenchmarks
{
    private const int Rows = 1_000_000;
    private const int BatchSize = 8192;
    private int[] _values = [];
    private int[] _definitionLevels = [];
    private int[] _repetitionLevels = [];
    private byte[] _file = [];
    private readonly int[] _readValues = new int[BatchSize];
    private readonly int[] _readDefinition = new int[BatchSize];
    private readonly int[] _readRepetition = new int[BatchSize];
    private ParquetSchema _schema = null!;
    private ColumnStructure _structure = null!;

    [GlobalSetup]
    public void Setup()
    {
        var values = new List<int>();
        var definition = new List<int>();
        var repetition = new List<int>();
        for (int i = 0; i < Rows; i++)
        {
            if (i % 11 == 0)
            {
                definition.Add(0); // a null list
                repetition.Add(0);
                continue;
            }

            int count = i % 5;
            if (count == 0)
            {
                definition.Add(1); // an empty list
                repetition.Add(0);
                continue;
            }

            for (int element = 0; element < count; element++)
            {
                definition.Add(3);
                repetition.Add(element == 0 ? 0 : 1);
                values.Add(i + element);
            }
        }

        _values = values.ToArray();
        _definitionLevels = definition.ToArray();
        _repetitionLevels = repetition.ToArray();
        var stream = new MemoryStream();
        WriteTo(stream);
        _file = stream.ToArray();
        _structure = ColumnStructure.FromLevels(_schema.Columns[0], _definitionLevels, _repetitionLevels);
    }

    [Benchmark]
    public long WriteFile()
    {
        using var stream = Stream.Null;
        return WriteTo(stream);
    }

    [Benchmark]
    public long ReadFile()
    {
        using var reader = new ParquetFileReader(new MemoryStream(_file));
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        long total = 0;
        BatchRead read;
        while ((read = column.ReadBatch(_readValues, _readDefinition, _readRepetition)).Entries > 0)
        {
            total += read.Values;
        }

        return total;
    }

    /// <summary>The levels of the whole column turned into validity and offsets.</summary>
    [Benchmark]
    public int LevelsToStructure() => ColumnStructure.FromLevels(_schema.Columns[0], _definitionLevels, _repetitionLevels).RowCount;

    /// <summary>The validity and offsets turned back into levels.</summary>
    [Benchmark]
    public int StructureToLevels()
    {
        _structure.GetLevels(out int[] definition, out _);
        return definition.Length;
    }

    private long WriteTo(Stream stream)
    {
        _schema = new ParquetSchema([NestedSchema.List("l", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional))]);
        using var writer = new ParquetFileWriter(stream, _schema, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(_values, _definitionLevels, _repetitionLevels);
        }

        writer.Close();
        return writer.Position;
    }
}
