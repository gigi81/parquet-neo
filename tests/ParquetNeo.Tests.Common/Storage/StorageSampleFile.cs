using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Storage;

/// <summary>A Parquet file for the tests of storages: ids in order, a text and a number, in row groups of small pages with a page index.</summary>
public static class StorageSampleFile
{
    /// <summary>The ids are <paramref name="firstId"/> up, the text of an id is <c>name-</c> and the id, and the number is half the id.</summary>
    public static byte[] Create(int rowGroups, int rowsPerGroup, int firstId = 0)
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("Name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
            new PrimitiveNode("Score", PhysicalType.Double, FieldRepetitionType.Required),
        ]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { WritePageIndex = true, DataPageSize = 4000 }, leaveOpen: true))
        {
            for (int group = 0; group < rowGroups; group++)
            {
                int start = firstId + (group * rowsPerGroup);
                var ids = Enumerable.Range(start, rowsPerGroup).ToArray();
                using var rowGroup = writer.AppendRowGroup();
                rowGroup.NextColumn().WriteBatch(ids);
                rowGroup.NextColumn().WriteStrings(ids.Select(id => $"name-{id}").ToArray());
                rowGroup.NextColumn().WriteBatch(ids.Select(id => id / 2.0).ToArray());
            }
        }

        return stream.ToArray();
    }
}
