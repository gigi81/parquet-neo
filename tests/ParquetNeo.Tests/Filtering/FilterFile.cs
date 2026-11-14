using ParquetNeo.ColumnTypes;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Filtering;

/// <summary>Writes a file of known row groups to read the statistics of, one batch of values for each column of each row group.</summary>
internal static class FilterFile
{
    /// <param name="fill">Writes the columns of a row group, which are written in the order of <paramref name="columns"/>.</param>
    public static ParquetFileReader Create(IReadOnlyList<PrimitiveNode> columns, int rowGroups, Action<int, RowGroupWriter> fill, ParquetWriterOptions? options = null) =>
        new(new MemoryStream(CreateBytes(columns, rowGroups, fill, options)));

    public static byte[] CreateBytes(IReadOnlyList<PrimitiveNode> columns, int rowGroups, Action<int, RowGroupWriter> fill, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema(columns), options, leaveOpen: true))
        {
            for (int group = 0; group < rowGroups; group++)
            {
                using var rowGroup = writer.AppendRowGroup();
                fill(group, rowGroup);
            }
        }

        return stream.ToArray();
    }

    public static PrimitiveNode Column<TValue>(string name, bool optional = false) =>
        ColumnTypeRegistry.Get<TValue>().CreateNode(name, optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required);
}
