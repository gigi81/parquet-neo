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

            if (read.Rows == 0)
            {
                return result.ToArray();
            }

            int next = 0;
            for (int row = 0; row < read.Rows; row++)
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

    public static ParquetFileReader Open(byte[] bytes, ParquetReaderOptions? options = null) =>
        new(new MemoryStream(bytes), options ?? new ParquetReaderOptions { Codecs = TestParquetFile.Registry });

    public static ParquetFileReader OpenFixture(string name, ParquetReaderOptions? options = null) =>
        Open(File.ReadAllBytes(TestBytes.FixturePath(name)), options);
}
