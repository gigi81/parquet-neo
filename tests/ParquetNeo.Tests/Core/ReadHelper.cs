using ParquetNeo.Core;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core;

/// <summary>Reads whole columns through <see cref="ColumnReader"/> in batches of a chosen size.</summary>
internal static class ReadHelper
{
    /// <summary>Returns one entry per row: null for a null, otherwise int, long, float, double, bool or byte[].</summary>
    public static object?[] ReadColumn(ParquetFileReader reader, int rowGroup, int column, int batchSize)
    {
        using var columnReader = reader.OpenRowGroup(rowGroup).OpenColumn(column);
        return ReadAll(columnReader, batchSize);
    }

    public static object?[] ReadAll(ColumnReader column, int batchSize)
    {
        bool optional = column.Column.MaxDefinitionLevel > 0;
        var result = new List<object?>();
        var levels = optional ? new int[batchSize] : [];
        var ints = new int[batchSize];
        var longs = new long[batchSize];
        var floats = new float[batchSize];
        var doubles = new double[batchSize];
        var bools = new bool[batchSize];
        var arrays = new ReadOnlyMemory<byte>[batchSize];
        int fixedLength = column.Column.TypeLength ?? 1;
        var fixedBytes = new byte[batchSize * fixedLength];

        while (true)
        {
            BatchRead read = column.Column.Type switch
            {
                PhysicalType.Int32 => column.ReadBatch(ints, levels),
                PhysicalType.Int64 => column.ReadBatch(longs, levels),
                PhysicalType.Float => column.ReadBatch(floats, levels),
                PhysicalType.Double => column.ReadBatch(doubles, levels),
                PhysicalType.Boolean => column.ReadBatch(bools, levels),
                PhysicalType.ByteArray => column.ReadByteArrays(arrays, levels),
                PhysicalType.FixedLenByteArray => column.ReadFixedLengthByteArrays(fixedBytes, levels),
                _ => throw new NotSupportedException(column.Column.Type.ToString()),
            };

            if (read.Entries == 0)
            {
                return result.ToArray();
            }

            int next = 0;
            for (int row = 0; row < read.Entries; row++)
            {
                if (optional && levels[row] != column.Column.MaxDefinitionLevel)
                {
                    result.Add(null);
                    continue;
                }

                result.Add(column.Column.Type switch
                {
                    PhysicalType.Int32 => ints[next],
                    PhysicalType.Int64 => longs[next],
                    PhysicalType.Float => floats[next],
                    PhysicalType.Double => doubles[next],
                    PhysicalType.Boolean => bools[next],
                    PhysicalType.ByteArray => arrays[next].ToArray(),
                    _ => fixedBytes.AsSpan(next * fixedLength, fixedLength).ToArray(),
                });
                next++;
            }

            if (next != read.Values)
            {
                throw new InvalidOperationException($"Read {read.Values} values but the levels mark {next} rows as non-null.");
            }
        }
    }

    /// <summary>Reads a whole column chunk with its levels: the definition and repetition level of every entry and the non-null values (byte arrays as <c>byte[]</c>).</summary>
    public static (int[] Definition, int[] Repetition, object[] Values) ReadEntries(ParquetFileReader reader, int rowGroup, int column, int batchSize)
    {
        using var columnReader = reader.OpenRowGroup(rowGroup).OpenColumn(column);
        var node = columnReader.Column;
        var definition = new List<int>();
        var repetition = new List<int>();
        var values = new List<object>();
        var definitionBuffer = node.MaxDefinitionLevel > 0 ? new int[batchSize] : [];
        var repetitionBuffer = node.MaxRepetitionLevel > 0 ? new int[batchSize] : [];
        var ints = new int[batchSize];
        var longs = new long[batchSize];
        var floats = new float[batchSize];
        var doubles = new double[batchSize];
        var bools = new bool[batchSize];
        var arrays = new ReadOnlyMemory<byte>[batchSize];
        int fixedLength = node.TypeLength ?? 1;
        var fixedBytes = new byte[batchSize * fixedLength];

        while (true)
        {
            BatchRead read = node.Type switch
            {
                PhysicalType.Int32 => columnReader.ReadBatch(ints, definitionBuffer, repetitionBuffer),
                PhysicalType.Int64 => columnReader.ReadBatch(longs, definitionBuffer, repetitionBuffer),
                PhysicalType.Float => columnReader.ReadBatch(floats, definitionBuffer, repetitionBuffer),
                PhysicalType.Double => columnReader.ReadBatch(doubles, definitionBuffer, repetitionBuffer),
                PhysicalType.Boolean => columnReader.ReadBatch(bools, definitionBuffer, repetitionBuffer),
                PhysicalType.ByteArray => columnReader.ReadByteArrays(arrays, definitionBuffer, repetitionBuffer),
                PhysicalType.FixedLenByteArray => columnReader.ReadFixedLengthByteArrays(fixedBytes, definitionBuffer, repetitionBuffer),
                _ => throw new NotSupportedException(node.Type.ToString()),
            };

            if (read.Entries == 0)
            {
                return (definition.ToArray(), repetition.ToArray(), values.ToArray());
            }

            if (node.MaxDefinitionLevel > 0)
            {
                definition.AddRange(definitionBuffer.AsSpan(0, read.Entries).ToArray());
            }
            else
            {
                definition.AddRange(Enumerable.Repeat(0, read.Entries));
            }

            repetition.AddRange(node.MaxRepetitionLevel > 0 ? repetitionBuffer.AsSpan(0, read.Entries).ToArray() : Enumerable.Repeat(0, read.Entries));
            for (int i = 0; i < read.Values; i++)
            {
                values.Add(node.Type switch
                {
                    PhysicalType.Int32 => ints[i],
                    PhysicalType.Int64 => longs[i],
                    PhysicalType.Float => floats[i],
                    PhysicalType.Double => doubles[i],
                    PhysicalType.Boolean => bools[i],
                    PhysicalType.ByteArray => arrays[i].ToArray(),
                    _ => fixedBytes.AsSpan(i * fixedLength, fixedLength).ToArray(),
                });
            }
        }
    }

    public static ParquetFileReader Open(byte[] bytes, ParquetReaderOptions? options = null) =>
        new(new MemoryStream(bytes), options ?? new ParquetReaderOptions { Codecs = TestParquetFile.Registry });

    public static ParquetFileReader OpenFixture(string name, ParquetReaderOptions? options = null) =>
        Open(File.ReadAllBytes(TestBytes.FixturePath(name)), options);
}
