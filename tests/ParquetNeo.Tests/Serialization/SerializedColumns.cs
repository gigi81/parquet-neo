using ParquetNeo.Metadata;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

/// <summary>Reads the columns of a serialized file back through the typed overloads of the column reader.</summary>
internal static class SerializedColumns
{
    /// <summary>Every value of the column over all row groups, in order, with null for a null.</summary>
    public static List<object?> Read(byte[] bytes, string column)
    {
        using var reader = ReadHelper.Open(bytes);
        var result = new List<object?>();
        for (int group = 0; group < reader.RowGroupCount; group++)
        {
            using var columnReader = reader.OpenRowGroup(group).OpenColumn(column);
            ReadAll(columnReader, result);
        }

        return result;
    }

    private static void ReadAll(ColumnReader reader, List<object?> result)
    {
        var node = reader.Column;
        switch (node.Type, node.LogicalType)
        {
            case (PhysicalType.Boolean, _): ReadAll<bool>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: true }): ReadAll<sbyte>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Integer, BitWidth: 8 }): ReadAll<byte>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Integer, BitWidth: 16, IsSigned: true }): ReadAll<short>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Integer, BitWidth: 16 }): ReadAll<ushort>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Integer }): ReadAll<uint>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int64, { Kind: LogicalTypeKind.Integer }): ReadAll<ulong>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int32, { Kind: LogicalTypeKind.Date }): ReadAll<DateOnly>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.Time }): ReadAll<TimeOnly>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.Timestamp }): ReadAll<DateTime>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.Decimal }): ReadAll<decimal>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.Uuid }): ReadAll<Guid>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.Float16 }): ReadAll<Half>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (_, { Kind: LogicalTypeKind.String }): ReadAll<string>(reader, result, (r, v, l) => r.ReadStrings(v, l)); break;
            case (PhysicalType.Int32, _): ReadAll<int>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Int64, _): ReadAll<long>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Float, _): ReadAll<float>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.Double, _): ReadAll<double>(reader, result, (r, v, l) => r.ReadBatch(v, l)); break;
            case (PhysicalType.ByteArray, _): ReadBinary(reader, result); break;
            default: throw new NotSupportedException(node.Type.ToString());
        }
    }

    private delegate BatchRead ReadBatch<T>(ColumnReader reader, T[] values, int[] levels);

    private static void ReadAll<T>(ColumnReader reader, List<object?> result, ReadBatch<T> read)
    {
        var values = new T[100];
        var levels = reader.Column.MaxDefinitionLevel > 0 ? new int[100] : [];
        while (true)
        {
            var batch = read(reader, values, levels);
            if (batch.Entries == 0)
            {
                return;
            }

            int next = 0;
            for (int entry = 0; entry < batch.Entries; entry++)
            {
                result.Add(levels.Length > 0 && levels[entry] == 0 ? null : values[next++]);
            }
        }
    }

    private static void ReadBinary(ColumnReader reader, List<object?> result)
    {
        var values = new ReadOnlyMemory<byte>[100];
        var levels = reader.Column.MaxDefinitionLevel > 0 ? new int[100] : [];
        while (true)
        {
            var batch = reader.ReadByteArrays(values, levels);
            if (batch.Entries == 0)
            {
                return;
            }

            int next = 0;
            for (int entry = 0; entry < batch.Entries; entry++)
            {
                result.Add(levels.Length > 0 && levels[entry] == 0 ? null : values[next++].ToArray());
            }
        }
    }
}
