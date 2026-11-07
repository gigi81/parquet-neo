using System.Text;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>Writes shredded rows to a file, optionally in awkwardly small batches.</summary>
internal static class NestedFile
{
    /// <summary>Writes <paramref name="rows"/> in row groups of <paramref name="rowsPerGroup"/> rows, passing each column to the writer in calls of at most <paramref name="batchEntries"/> entries.</summary>
    public static byte[] Write(
        ParquetSchema schema,
        IReadOnlyList<StructValue> rows,
        ParquetWriterOptions? options = null,
        int rowsPerGroup = int.MaxValue,
        int batchEntries = int.MaxValue)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        {
            for (int start = 0; start < rows.Count; start += rowsPerGroup)
            {
                var shredded = ReferenceShredder.Shred(schema, rows.Skip(start).Take(rowsPerGroup));
                using var group = writer.AppendRowGroup();
                foreach (var column in shredded)
                {
                    WriteColumn(group.NextColumn(), column, batchEntries);
                }
            }
        }

        return stream.ToArray();
    }

    public static void WriteColumn(ColumnWriter writer, ShreddedColumn column, int batchEntries = int.MaxValue)
    {
        var node = column.Column;
        var definition = column.Definition.ToArray();
        var repetition = column.Repetition.ToArray();
        int total = definition.Length;
        int valueStart = 0;
        for (int start = 0; start < total; start += batchEntries)
        {
            int count = (int)Math.Min(batchEntries, (long)total - start);
            var definitionSlice = definition.AsSpan(start, count);
            var repetitionSlice = repetition.AsSpan(start, count);
            int valueCount = node.MaxDefinitionLevel == 0 ? count : definitionSlice.Count(node.MaxDefinitionLevel);
            var values = column.Values.GetRange(valueStart, valueCount);
            valueStart += valueCount;

            // A required, non-repeated column takes no levels.
            var definitionLevels = node.MaxDefinitionLevel == 0 ? default : definitionSlice;
            var repetitionLevels = node.MaxRepetitionLevel == 0 ? default : repetitionSlice;
            switch (node.Type)
            {
                case PhysicalType.Int32: writer.WriteBatch(values.Cast<int>().ToArray(), definitionLevels, repetitionLevels); break;
                case PhysicalType.Int64: writer.WriteBatch(values.Cast<long>().ToArray(), definitionLevels, repetitionLevels); break;
                case PhysicalType.Float: writer.WriteBatch(values.Cast<float>().ToArray(), definitionLevels, repetitionLevels); break;
                case PhysicalType.Double: writer.WriteBatch(values.Cast<double>().ToArray(), definitionLevels, repetitionLevels); break;
                case PhysicalType.Boolean: writer.WriteBatch(values.Cast<bool>().ToArray(), definitionLevels, repetitionLevels); break;
                case PhysicalType.ByteArray: writer.WriteStrings(values.Cast<string>().ToArray(), definitionLevels, repetitionLevels); break;
                default: throw new NotSupportedException(node.Type.ToString());
            }
        }
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
