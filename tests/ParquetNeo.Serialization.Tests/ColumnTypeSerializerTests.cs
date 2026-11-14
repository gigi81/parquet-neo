using ParquetNeo.ColumnTypes;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests;

namespace ParquetNeo.Serialization.Tests;

/// <summary>The serializer with column types that are not its own: the library's fixed-length binary and one a user registers.</summary>
public class ColumnTypeSerializerTests
{
    private static readonly ColumnType<byte[]> Binary = ColumnTypeRegistry.Get<byte[]>();

    public sealed class Hashed
    {
        public int Id { get; set; }

        public byte[] Hash { get; set; } = [];

        public byte[]? Optional { get; set; }
    }

    public sealed class Invoice
    {
        public string Number { get; set; } = "";

        public Money Total { get; set; }

        public Money? Discount { get; set; }

        public List<Money> Lines { get; set; } = [];
    }

    [Test]
    public async Task TheSerializer_ReadsAFixedLengthColumnIntoAByteArrayProperty()
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("Hash", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, typeLength: 3),
            new PrimitiveNode("Optional", PhysicalType.FixedLenByteArray, FieldRepetitionType.Optional, typeLength: 2),
        ]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options: null, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2]);
            group.NextColumn().WriteBatch(Binary, [new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }]);
            group.NextColumn().WriteBatch(Binary, [new byte[] { 9, 9 }], [0, 1]);
        }

        stream.Position = 0;
        var rows = ParquetSerializer.Deserialize<Hashed>(stream);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Hash).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rows[1].Hash).IsEquivalentTo(new byte[] { 4, 5, 6 });
        await Assert.That(rows[0].Optional).IsNull();
        await Assert.That(rows[1].Optional).IsEquivalentTo(new byte[] { 9, 9 });
    }

    [Test]
    public async Task ARegisteredColumnType_TeachesTheSerializerANewType()
    {
        ColumnTypeRegistry.Register(new MoneyColumnType());
        var invoices = new List<Invoice>
        {
            new() { Number = "A", Total = new Money(1000), Discount = new Money(50), Lines = [new Money(400), new Money(600)] },
            new() { Number = "B", Total = new Money(7), Discount = null, Lines = [] },
        };

        var stream = new MemoryStream();
        ParquetSerializer.Serialize(invoices, stream);
        stream.Position = 0;
        var back = ParquetSerializer.Deserialize<Invoice>(stream);

        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back[0].Total).IsEqualTo(new Money(1000));
        await Assert.That(back[0].Discount).IsEqualTo(new Money(50));
        await Assert.That(back[0].Lines).IsEquivalentTo(new[] { new Money(400), new Money(600) });
        await Assert.That(back[1].Discount).IsNull();
        await Assert.That(back[1].Lines).IsEmpty();
        await Assert.That(ColumnTypeRegistry.IsSupported(typeof(Money))).IsTrue();
        await Assert.That(() => ColumnTypeRegistry.Register<Money>(null!)).Throws<ArgumentNullException>();
    }
}
