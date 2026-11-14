using ParquetNeo.AotSmoke;
using ParquetNeo.Compression;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;

namespace ParquetNeo.Tests.Serialization;

/// <summary>
/// A mapping that is registered for a type is used instead of reflection. The one the source generator writes for <see cref="Order"/>
/// (marked <c>[ParquetSerializable]</c>) is checked against the reflection path, which handles <see cref="Twin"/>, a type with the
/// same properties that has no mapping: both must write the same bytes and read the same rows.
/// </summary>
public class GeneratedMappingTests
{
    private sealed class Twin
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public double Score { get; set; }

        public OrderStatus Status { get; set; }

        public int? Quantity { get; set; }
    }

    private sealed class TwinWithNullableScore
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public double? Score { get; set; }

        public OrderStatus Status { get; set; }

        public int? Quantity { get; set; }
    }

    private static Order[] Orders() =>
    [
        new() { Id = 1, Name = "Ada", Score = 0.5, Status = OrderStatus.New, Quantity = 3 },
        new() { Id = 2, Name = null, Score = 1.5, Status = OrderStatus.Shipped, Quantity = null },
        new() { Id = 3, Name = "", Score = -2.5, Status = OrderStatus.Returned, Quantity = 0 },
        new() { Id = 4, Name = "Grace", Score = double.MaxValue, Status = OrderStatus.New, Quantity = int.MinValue },
    ];

    private static Twin[] Twins() => Orders().Select(o => new Twin { Id = o.Id, Name = o.Name, Score = o.Score, Status = o.Status, Quantity = o.Quantity }).ToArray();

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, CompressionCodec codec = CompressionCodec.Uncompressed, int rowGroupSize = 100_000)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = codec }, RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    [Test]
    public async Task TheSchema_IsTheOneTheReflectionPathMakes()
    {
        var generated = ParquetSerializer.GetSchema<Order>();
        var reflected = ParquetSerializer.GetSchema<Twin>();

        await Assert.That(generated.Root.Children.Select(c => (c.Name, c.Repetition)).ToArray())
            .IsEquivalentTo(reflected.Root.Children.Select(c => (c.Name, c.Repetition)).ToArray());
        await Assert.That(generated.Root.Children.Count).IsEqualTo(5);
    }

    [Test]
    [Arguments(CompressionCodec.Uncompressed, 100_000)]
    [Arguments(CompressionCodec.Gzip, 100_000)]
    [Arguments(CompressionCodec.Brotli, 3)]
    [Arguments(CompressionCodec.Uncompressed, 1)]
    public async Task TheFileWritten_IsByteForByteWhatTheReflectionPathWrites(CompressionCodec codec, int rowGroupSize)
    {
        byte[] generated = Serialize(Orders(), codec, rowGroupSize);
        byte[] reflected = Serialize(Twins(), codec, rowGroupSize);

        await Assert.That(generated.AsSpan().SequenceEqual(reflected)).IsTrue();
    }

    [Test]
    public async Task TheRowsRead_AreTheRowsWritten_WhoeverWroteTheFile()
    {
        foreach (var bytes in new[] { Serialize(Orders()), Serialize(Twins()) })
        {
            var read = ParquetSerializer.Deserialize<Order>(new MemoryStream(bytes));

            await Assert.That(read.Count).IsEqualTo(4);
            for (int i = 0; i < read.Count; i++)
            {
                var (expected, actual) = (Orders()[i], read[i]);
                await Assert.That((actual.Id, actual.Name, actual.Score, actual.Status, actual.Quantity))
                    .IsEqualTo((expected.Id, expected.Name, expected.Score, expected.Status, expected.Quantity));
            }
        }
    }

    [Test]
    public async Task ANullInAColumnTheRowCannotHold_IsRefusedLikeTheReflectionPathDoes()
    {
        var rows = new[] { new TwinWithNullableScore { Id = 1, Score = 1 }, new TwinWithNullableScore { Id = 2, Score = null } };
        byte[] bytes = Serialize(rows);

        var generated = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<Order>(new MemoryStream(bytes)));
        var reflected = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<Twin>(new MemoryStream(bytes)));

        await Assert.That(generated.Message).Contains("Score").And.Contains("row 1");
        await Assert.That(generated.Message.Replace("Order", "Twin", StringComparison.Ordinal)).IsEqualTo(reflected.Message);
    }

    [Test]
    public async Task AFileOfAnotherShape_IsRefusedBeforeAnyRowIsRead()
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(new[] { new Wrong { Id = 1 } }, stream);
        stream.Position = 0;

        var exception = Assert.Throws<SchemaMismatchException>(() => ParquetSerializer.Deserialize<Order>(stream));

        await Assert.That(exception.Message).Contains("'Score'");
    }

    private sealed class Wrong
    {
        public long Id { get; set; }
    }
}
