using ParquetNeo;
using ParquetNeo.AotSmoke;
using ParquetNeo.Core;
using ParquetNeo.Compression;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization;
using ParquetNeo.Schema;

// The column API with no reflection: write two row groups of a few types, read them back, compare.
var schema = new ParquetSchema(
[
    new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
    new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
    new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Required),
]);

long[] ids = [1, 2, 3, 4];
string[] names = ["Ada", "Alan", "Grace"];
int[] nameLevels = [1, 1, 0, 1];
double[] scores = [0.5, 1.5, 2.5, 3.5];

var stream = new MemoryStream();
using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
{
    for (int group = 0; group < 2; group++)
    {
        using var rowGroup = writer.AppendRowGroup();
        rowGroup.NextColumn().WriteBatch(ids);
        rowGroup.NextColumn().WriteStrings(names, nameLevels);
        rowGroup.NextColumn().WriteBatch(scores);
    }
}

stream.Position = 0;
using var reader = new ParquetFileReader(stream);
for (int group = 0; group < reader.RowGroupCount; group++)
{
    var rowGroup = reader.OpenRowGroup(group);
    var readIds = new long[4];
    using (var column = rowGroup.OpenColumn(0))
    {
        column.ReadBatch(readIds);
    }

    if (!readIds.AsSpan().SequenceEqual(ids))
    {
        Console.WriteLine("ids differ");
        return 1;
    }

    var readNames = new string[4];
    var readLevels = new int[4];
    using (var column = rowGroup.OpenColumn(1))
    {
        column.ReadStrings(readNames, readLevels);
    }

    if (!readNames.AsSpan(0, 3).SequenceEqual(names) || !readLevels.AsSpan().SequenceEqual(nameLevels))
    {
        Console.WriteLine("names differ");
        return 1;
    }
}

// The row serializer with a mapping written by hand (the generator's job later): no reflection, no code compiled at run time.
OrderMapping.Register();
Order[] orders =
[
    new() { Id = 1, Name = "Ada", Score = 0.5, Status = OrderStatus.New, Quantity = 3 },
    new() { Id = 2, Name = null, Score = 1.5, Status = OrderStatus.Shipped, Quantity = null },
    new() { Id = 3, Name = "Grace", Score = 2.5, Status = OrderStatus.Returned, Quantity = 0 },
];

foreach (var codec in new[] { CompressionCodec.Uncompressed, CompressionCodec.Gzip, CompressionCodec.Brotli })
{
    var file = new MemoryStream();
    ParquetSerializer.Serialize(orders, file, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = codec }, RowGroupSize = 2 });
    file.Position = 0;
    var read = ParquetSerializer.Deserialize<Order>(file);
    if (read.Count != orders.Length)
    {
        Console.WriteLine($"{codec}: {read.Count} rows instead of {orders.Length}");
        return 1;
    }

    for (int i = 0; i < orders.Length; i++)
    {
        var (a, b) = (orders[i], read[i]);
        if (a.Id != b.Id || a.Name != b.Name || a.Score != b.Score || a.Status != b.Status || a.Quantity != b.Quantity)
        {
            Console.WriteLine($"{codec}: row {i} differs");
            return 1;
        }
    }
}

// A type with no mapping is refused with an explanation, not a crash from the trimmer.
try
{
    ParquetSerializer.Serialize(new[] { new Unmapped() }, new MemoryStream());
    Console.WriteLine("a type without a mapping was written");
    return 1;
}
catch (NotSupportedException exception)
{
    Console.WriteLine(exception.Message);
}

Console.WriteLine("ok");
return 0;

internal sealed class Unmapped
{
    public int Value { get; set; }
}
