using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Tests.Assemblers;

public class StructAssemblerTests
{
    private sealed class Address
    {
        public string City { get; set; } = "";

        public int Zip { get; set; }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public Address? Billing { get; set; }

        public Address Shipping { get; set; } = new();
    }

    private sealed class OrderWithoutAddresses
    {
        public int Id { get; set; }
    }

    private struct Coordinates
    {
        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class Place
    {
        public Coordinates Where { get; set; }

        public Coordinates? Elsewhere { get; set; }
    }

    private static T[] ReadAll<T>(AssemblerHarness file, int rows)
    {
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<T>();
        var context = file.Read(builder, row);
        return [.. Enumerable.Range(0, rows).Select(_ => row.ReadRow(context))];
    }

    [Test]
    public async Task ARowIsBuiltFromTheColumnsOfItsObjects_AndTheRowNumberCountsUp()
    {
        using var file = AssemblerHarness.Write(
        [
            new Order { Id = 1, Billing = new Address { City = "Pisa", Zip = 56100 }, Shipping = new Address { City = "Lucca", Zip = 55100 } },
            new Order { Id = 2, Billing = null, Shipping = new Address { City = "Siena", Zip = 53100 } },
        ]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Order>();
        var context = file.Read(builder, row);

        var first = row.ReadRow(context);
        await Assert.That(context.Row).IsEqualTo(1);
        var second = row.ReadRow(context);

        await Assert.That(context.Row).IsEqualTo(2);
        await Assert.That(first.Billing!.City).IsEqualTo("Pisa");
        await Assert.That(first.Shipping.Zip).IsEqualTo(55100);
        await Assert.That(second.Billing).IsNull();
        await Assert.That(second.Shipping.City).IsEqualTo("Siena");
    }

    [Test]
    public async Task AnOptionalObjectTheFileLacks_IsNull()
    {
        using var file = AssemblerHarness.Write([new OrderWithoutAddresses { Id = 9 }]);

        var order = ReadAll<OptionalBilling>(file, 1)[0];

        await Assert.That(order.Id).IsEqualTo(9);
        await Assert.That(order.Billing).IsNull();
    }

    [Test]
    public async Task ARequiredObjectTheFileLacks_IsRefused()
    {
        using var file = AssemblerHarness.Write([new OrderWithoutAddresses { Id = 9 }]);
        var builder = new AssemblerBuilder();

        var exception = Assert.Throws<SchemaMismatchException>(() => file.Read(builder, builder.BuildRow<Order>()));

        await Assert.That(exception.Message).Contains("has no column named 'Shipping'");
    }

    private sealed class OptionalBilling
    {
        public int Id { get; set; }

        public Address? Billing { get; set; }
    }

    [Test]
    public async Task StructsAreBuiltToo_AsPropertiesAndAsNullableProperties()
    {
        using var file = AssemblerHarness.Write(
        [
            new Place { Where = new Coordinates { X = 1.5, Y = 2.5 }, Elsewhere = new Coordinates { X = 3, Y = 4 } },
            new Place { Where = new Coordinates { X = -1, Y = -2 }, Elsewhere = null },
        ]);

        var places = ReadAll<Place>(file, 2);

        await Assert.That(places[0].Where).IsEqualTo(new Coordinates { X = 1.5, Y = 2.5 });
        await Assert.That(places[0].Elsewhere).IsEqualTo(new Coordinates { X = 3, Y = 4 });
        await Assert.That(places[1].Where).IsEqualTo(new Coordinates { X = -1, Y = -2 });
        await Assert.That(places[1].Elsewhere).IsNull();
    }

    private sealed class AsNumber
    {
        public int Billing { get; set; }
    }

    private sealed class AsList
    {
        public List<int> Billing { get; set; } = [];
    }

    private sealed class AsMap
    {
        public Dictionary<string, int> Billing { get; set; } = [];
    }

    private sealed class OnlyBilling
    {
        public Address? Billing { get; set; }
    }

    [Test]
    public async Task ASingleColumn_CannotBeReadAsAnObject()
    {
        using var file = AssemblerHarness.Write([new AsNumber { Billing = 1 }]);
        var builder = new AssemblerBuilder();

        var exception = Assert.Throws<SchemaMismatchException>(() => file.Read(builder, builder.BuildRow<OnlyBilling>()));

        await Assert.That(exception.Message).Contains("The column 'Billing' of the file");
        await Assert.That(exception.Message).Contains("is a single column, but the property is an object");
    }

    [Test]
    public async Task AListOrAMap_CannotBeReadAsAnObject()
    {
        using var list = AssemblerHarness.Write([new AsList { Billing = [1] }]);
        using var map = AssemblerHarness.Write([new AsMap { Billing = new Dictionary<string, int> { ["a"] = 1 } }]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<OnlyBilling>();

        var fromList = Assert.Throws<SchemaMismatchException>(() => list.Read(builder, row));
        var fromMap = Assert.Throws<SchemaMismatchException>(() => map.Read(builder, row));

        await Assert.That(fromList.Message).Contains("is a LIST, but the property is an object");
        await Assert.That(fromMap.Message).Contains("is a MAP, but the property is an object");
    }

    private sealed class RequiredCity
    {
        public string City { get; set; } = "";
    }

    private sealed class WithRequiredCity
    {
        public RequiredCity Address { get; set; } = new();
    }

    private sealed class ZipOnly
    {
        public int Zip { get; set; }
    }

    private sealed class WithZipOnly
    {
        public ZipOnly Address { get; set; } = new();
    }

    [Test]
    public async Task ARequiredFieldOfAGroupTheFileLacks_NamesTheGroupAndTheField()
    {
        using var file = AssemblerHarness.Write([new WithZipOnly { Address = new ZipOnly { Zip = 1 } }]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<WithRequiredCity>();

        var exception = Assert.Throws<SchemaMismatchException>(() => file.Read(builder, row));

        await Assert.That(exception.Message).Contains("The group 'Address' of the file has no field named 'City'");
        await Assert.That(exception.Message).Contains("'Zip'");
    }

    [Test]
    public async Task ItAddsOneContextForTheValuesWaitingForAConstructor_AndNoneOtherwise()
    {
        var direct = new List<object>();
        var constructed = new List<object>();

        new AssemblerBuilder().BuildRow<Address>().AddState([], direct);
        new AssemblerBuilder().BuildRow<Person>().AddState([], constructed);

        await Assert.That(direct.Count).IsEqualTo(0);
        await Assert.That(constructed.Count).IsEqualTo(1);
        await Assert.That(constructed[0]).IsTypeOf<RowContext>();
    }

    private sealed record Person(string Name, int Age);
}
