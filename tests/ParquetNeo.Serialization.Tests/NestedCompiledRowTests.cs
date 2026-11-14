using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;

namespace ParquetNeo.Serialization.Tests;

/// <summary>Rows with objects, collections and dictionaries inside are shredded and assembled by generated code too; these check it against what was written.</summary>
public class NestedCompiledRowTests
{
    private sealed class Line
    {
        public string Sku { get; set; } = "";

        public int? Quantity { get; set; }

        public List<string?>? Notes { get; set; }
    }

    private sealed class Address
    {
        public string City { get; set; } = "";

        public int Zip { get; set; }
    }

    private sealed class Customer
    {
        public string Name { get; set; } = "";

        public Address? Home { get; set; }

        public Address Work { get; set; } = new();
    }

    private readonly record struct Point(double X, double Y);

    private sealed class Order
    {
        public long Id { get; set; }

        public Customer? Customer { get; set; }

        public Point Origin { get; set; }

        public Point? Destination { get; set; }

        public List<Line> Lines { get; set; } = [];

        public double[] Scores { get; set; } = [];

        public IReadOnlyList<int>? History { get; set; }

        public List<List<int?>?>? Matrix { get; set; }

        public Dictionary<string, Line?>? ByName { get; set; }
    }

    private static Order[] Orders(int count) =>
        Enumerable.Range(0, count).Select(i => new Order
        {
            Id = i,
            Customer = i % 4 == 0 ? null : new Customer
            {
                Name = "customer-" + (i % 17),
                Home = i % 3 == 0 ? null : new Address { City = "city-" + (i % 5), Zip = 1000 + i },
                Work = new Address { City = "work-" + (i % 7), Zip = 2000 + i },
            },
            Origin = new Point(i, -i),
            Destination = i % 2 == 0 ? null : new Point(i * 0.5, i * 0.25),
            Lines = Enumerable.Range(0, i % 4).Select(t => new Line
            {
                Sku = "sku-" + ((i + t) % 11),
                Quantity = t % 2 == 0 ? t : null,
                Notes = t == 1 ? null : Enumerable.Range(0, t).Select(n => n % 2 == 0 ? "note-" + n : null).ToList(),
            }).ToList(),
            Scores = Enumerable.Range(0, i % 5).Select(s => i + s * 0.5).ToArray(),
            History = i % 6 == 0 ? null : Enumerable.Range(0, i % 3).ToList(),
            Matrix = i % 5 == 0 ? null : Enumerable.Range(0, i % 3).Select(r => r == 1 ? null : Enumerable.Range(0, r + 1).Select(c => c % 2 == 0 ? (int?)(i + c) : null).ToList()).ToList(),
            ByName = i % 4 == 1 ? null : Enumerable.Range(0, i % 3).ToDictionary(k => "key-" + k, k => k == 1 ? null : new Line { Sku = "dict-" + k, Quantity = k }),
        }).ToArray();

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = 100 });
        return stream.ToArray();
    }

    private static List<TRow> Read<TRow>(byte[] bytes) => ParquetSerializer.Deserialize<TRow>(new MemoryStream(bytes));

    [Test]
    public async Task ObjectsCollectionsAndDictionaries_RoundTripAtEveryDepth()
    {
        var orders = Orders(500);

        var read = Read<Order>(Serialize(orders));

        await Assert.That(read.Count).IsEqualTo(orders.Length);
        for (int i = 0; i < orders.Length; i++)
        {
            var a = orders[i];
            var b = read[i];
            await Assert.That(b.Id).IsEqualTo(a.Id);
            await Assert.That(b.Customer is null).IsEqualTo(a.Customer is null).Because($"row {i}");
            await Assert.That(b.Customer?.Name).IsEqualTo(a.Customer?.Name);
            await Assert.That(b.Customer?.Home?.Zip).IsEqualTo(a.Customer?.Home?.Zip).Because($"row {i}");
            await Assert.That(b.Customer?.Work.City).IsEqualTo(a.Customer?.Work.City);
            await Assert.That(b.Origin).IsEqualTo(a.Origin);
            await Assert.That(b.Destination).IsEqualTo(a.Destination);
            await Assert.That(b.Lines.Select(l => (l.Sku, l.Quantity, Notes: l.Notes is null ? "null" : string.Join('|', l.Notes.Select(n => n ?? "∅")))))
                .IsEquivalentTo(a.Lines.Select(l => (l.Sku, l.Quantity, Notes: l.Notes is null ? "null" : string.Join('|', l.Notes.Select(n => n ?? "∅")))));
            await Assert.That(b.Scores).IsEquivalentTo(a.Scores);
            await Assert.That(Describe(b.History)).IsEqualTo(Describe(a.History)).Because($"row {i}");
            await Assert.That(Describe(b.Matrix)).IsEqualTo(Describe(a.Matrix)).Because($"row {i}");
            await Assert.That(b.ByName is null).IsEqualTo(a.ByName is null);
            await Assert.That(Describe(b.ByName)).IsEqualTo(Describe(a.ByName)).Because($"row {i}");
        }
    }

    private static string Describe(IReadOnlyList<int>? history) => history is null ? "null" : string.Join(',', history);

    private static string Describe(Dictionary<string, Line?>? map) =>
        map is null ? "null" : string.Join(';', map.Select(p => $"{p.Key}={p.Value?.Sku}/{p.Value?.Quantity}"));

    private static string Describe(List<List<int?>?>? matrix) =>
        matrix is null ? "null" : string.Join(';', matrix.Select(row => row is null ? "null" : string.Join(',', row.Select(c => c?.ToString() ?? "∅"))));

    [Test]
    public async Task TheListsAndArraysThatAreRead_AreSeparateAndExactlyAsBigAsTheirElements()
    {
        var read = Read<Order>(Serialize(Orders(60)));

        var lines = read.Select(o => o.Lines).ToList();
        await Assert.That(lines.Distinct().Count()).IsEqualTo(lines.Count);
        await Assert.That(lines.All(l => l.Capacity == l.Count)).IsTrue();
        await Assert.That(read.Select(o => o.Scores).Distinct().Count()).IsGreaterThan(1);

        // Row 4 has the longest array in the first few rows, and the scratch has held it: the shorter ones that come after are not padded.
        await Assert.That(read[5].Scores.Length).IsEqualTo(0);
        await Assert.That(read[6].Scores.Length).IsEqualTo(1);
    }

    [Test]
    public async Task ABatchOfRows_ReadsTheSameWhateverTheRowGroupSize()
    {
        var orders = Orders(250);
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(orders, stream, new ParquetSerializerOptions { RowGroupSize = 37 });

        var read = Read<Order>(stream.ToArray());

        await Assert.That(read.Select(o => o.Id)).IsEquivalentTo(orders.Select(o => o.Id));
        await Assert.That(read.Select(o => o.Customer?.Home?.City)).IsEquivalentTo(orders.Select(o => o.Customer?.Home?.City));
    }

    private sealed class RequiredAddress
    {
        public long Id { get; set; }

        public Address Address { get; set; } = new();
    }

    [Test]
    public async Task ARequiredObjectThatIsNull_IsRefusedWithItsNameAndTheRow()
    {
        var rows = new[] { new RequiredAddress { Id = 1 }, new RequiredAddress { Id = 2 }, new RequiredAddress { Id = 3, Address = null! } };

        var exception = Assert.Throws<ArgumentException>(() => Serialize(rows));

        await Assert.That(exception.Message).Contains("RequiredAddress.Address");
        await Assert.That(exception.Message).Contains("row 2");
    }

    private sealed class OptionalAddress
    {
        public long Id { get; set; }

        public Address? Address { get; set; }
    }

    [Test]
    public async Task AnObjectThatIsOptionalInTheFile_ButRequiredInTheRowType_NamesTheRowOfTheFile()
    {
        var bytes = Serialize(new[] { new OptionalAddress { Id = 1, Address = new Address { City = "a", Zip = 1 } }, new OptionalAddress { Id = 2 } });

        var exception = Assert.Throws<ValueConversionException>(() => Read<RequiredAddress>(bytes));

        await Assert.That(exception.Message).Contains("Address");
        await Assert.That(exception.Message).Contains("row 1");
    }
}
