using BenchmarkDotNet.Attributes;
using ParquetNeo.Serialization;

namespace ParquetNeo.Benchmarks;

/// <summary>
/// The serializer with rows that have objects, lists and arrays inside (200,000 orders, each with an optional customer, an address,
/// tags, scores and order lines), to a memory stream and back. The comparison in <c>Comparison</c> has flat rows.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class NestedSerializerBenchmarks
{
    private const int Rows = 200_000;
    private Order[] _orders = [];
    private byte[] _file = [];
    private MemoryStream _output = null!;
    private readonly ParquetSerializerOptions _options = new() { RowGroupSize = Rows };

    [GlobalSetup]
    public void Setup()
    {
        _orders = new Order[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _orders[i] = new Order
            {
                Id = i,
                Customer = i % 10 == 0 ? null : new Customer { Name = "customer-" + (i % 5000), Age = 20 + (i % 50), Vip = i % 7 == 0 },
                Address = new Address { City = "city-" + (i % 300), Zip = 10000 + (i % 9000) },
                Amount = i % 3 == 0 ? null : i * 0.25,
                Tags = i % 4 == 0 ? null : Enumerable.Range(0, i % 4).Select(t => "tag-" + (t + (i % 20))).ToList(),
                Scores = Enumerable.Range(0, i % 5).Select(t => (double)(i + t)).ToArray(),
                Lines = Enumerable.Range(0, i % 4).Select(t => new Line { Sku = "sku-" + ((i + t) % 1000), Quantity = 1 + t, Price = 9.99 + t }).ToList(),
            };
        }

        _output = new MemoryStream(32 * 1024 * 1024);
        ParquetSerializer.Serialize(_orders, _output, _options);
        _file = _output.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => _output.Dispose();

    [Benchmark]
    public long Serialize()
    {
        _output.SetLength(0);
        ParquetSerializer.Serialize(_orders, _output, _options);
        return _output.Length;
    }

    [Benchmark]
    public int Deserialize() => ParquetSerializer.Deserialize<Order>(new MemoryStream(_file)).Count;

    public sealed class Order
    {
        public long Id { get; set; }

        public Customer? Customer { get; set; }

        public Address Address { get; set; } = new();

        public double? Amount { get; set; }

        public List<string>? Tags { get; set; }

        public double[] Scores { get; set; } = [];

        public List<Line> Lines { get; set; } = [];
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";

        public int Age { get; set; }

        public bool Vip { get; set; }
    }

    public sealed class Address
    {
        public string City { get; set; } = "";

        public int Zip { get; set; }
    }

    public sealed class Line
    {
        public string Sku { get; set; } = "";

        public int Quantity { get; set; }

        public double Price { get; set; }
    }
}
