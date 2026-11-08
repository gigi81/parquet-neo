using ParquetNeo.Core;
using ParquetNeo.DataModel;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class DictionaryAttributeTests
{
    private sealed class Order
    {
        [ParquetDictionary(DictionaryMode.Disabled)]
        public long Id { get; set; }

        [ParquetDictionary(DictionaryMode.Adaptive)]
        public string Reference { get; set; } = "";

        public string Country { get; set; } = "";

        [ParquetDictionary(DictionaryMode.Adaptive)]
        public List<long> Tickets { get; set; } = [];
    }

    private static Order[] Orders(int count) =>
        Enumerable.Range(0, count).Select(i => new Order
        {
            Id = i * 31L,
            Reference = "reference-" + i,
            Country = "country-" + (i % 7),
            Tickets = Enumerable.Range(0, i % 3).Select(t => i * 100L + t).ToList(),
        }).ToArray();

    private static byte[] Serialize(Order[] orders, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(orders, stream, options);
        return stream.ToArray();
    }

    [Test]
    public async Task TheAttributes_ChooseHowEachColumnIsDictionaryEncoded()
    {
        var orders = Orders(30_000);
        var bytes = Serialize(orders);
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        // Id: no dictionary. Reference and Tickets: dropped after the first values. Country: repeats, kept to the end.
        await Assert.That(file.Chunk(0, 0).DictionaryPageOffset).IsNull();
        await Assert.That(file.DataPages(0, 1).Select(p => p.Header.DataPageHeader!.Encoding).Distinct()).IsEquivalentTo([ParquetNeo.Metadata.ParquetEncoding.RleDictionary, ParquetNeo.Metadata.ParquetEncoding.Plain]);
        await Assert.That(file.DataPages(0, 2).Select(p => p.Header.DataPageHeader!.Encoding).Distinct()).IsEquivalentTo([ParquetNeo.Metadata.ParquetEncoding.RleDictionary]);
        await Assert.That(file.Chunk(0, 3).DictionaryPageOffset).IsNotNull();

        var read = ParquetSerializer.Deserialize<Order>(new MemoryStream(bytes)).ToList();
        await Assert.That(read.Select(o => o.Id)).IsEquivalentTo(orders.Select(o => o.Id));
        await Assert.That(read.Select(o => o.Reference)).IsEquivalentTo(orders.Select(o => o.Reference));
        await Assert.That(read.Select(o => o.Country)).IsEquivalentTo(orders.Select(o => o.Country));
        await Assert.That(read.Select(o => string.Join(',', o.Tickets))).IsEquivalentTo(orders.Select(o => string.Join(',', o.Tickets)));
    }

    [Test]
    public async Task TheOptions_WinOverTheAttributes()
    {
        var options = new ParquetSerializerOptions
        {
            Writer = new ParquetWriterOptions { ColumnDictionaries = new Dictionary<string, DictionaryMode> { ["Id"] = DictionaryMode.Standard } },
        };

        var file = TestParquetFile.Parse(Serialize(Orders(1000), options));

        await Assert.That(file.Chunk(0, 0).DictionaryPageOffset).IsNotNull();
    }
}
