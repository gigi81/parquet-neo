using ParquetNeo.DataModel;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;

namespace ParquetNeo.Serialization.Tests;

public class DeserializerModelTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    private static List<TRow> RoundTrip<TRow>(IEnumerable<TRow> rows) => ParquetSerializer.Deserialize<TRow>(new MemoryStream(Serialize(rows)));

    // ---- how rows are created ----

    private sealed record Person(string Name, int Age, string? Email);

    private readonly struct Reading
    {
        public Reading(int sensor, decimal? value)
        {
            Sensor = sensor;
            Value = value;
        }

        public int Sensor { get; }

        [ParquetDecimal(10, 3)]
        public decimal? Value { get; }
    }

    private record struct Point(double X, double Y);

    private struct MutablePoint
    {
        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class WithInitOnly
    {
        public int Id { get; init; }

        public string Label { get; init; } = "default";

        public string? Note { get; init; } = "kept unless the file says null";
    }

    private sealed class WithPrivateSetter
    {
        public int Id { get; private set; }

        public string? Name { get; set; }

        public int Computed => Id * 2;
    }

    private sealed class WithConstructorAndSetter
    {
        public WithConstructorAndSetter(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public string? Name { get; set; }
    }

    private sealed record WithExtraProperty(int Id)
    {
        public string? Name { get; init; }
    }

    [Test]
    public async Task Records_AreBuiltByTheirConstructor()
    {
        var rows = RoundTrip([new Person("Ann", 30, null), new Person("Bob", 41, "bob@example.com")]);

        await Assert.That(rows).IsEquivalentTo(new[] { new Person("Ann", 30, null), new Person("Bob", 41, "bob@example.com") });
    }

    [Test]
    public async Task AStructWithAConstructor_IsBuiltByIt()
    {
        var rows = RoundTrip([new Reading(1, 1.5m), new Reading(2, null), new Reading(3, -0.001m)]);

        await Assert.That(rows.Select(r => r.Sensor)).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That(rows.Select(r => r.Value)).IsEquivalentTo(new decimal?[] { 1.5m, null, -0.001m });
    }

    [Test]
    public async Task Structs_WithSetters_AreSetInPlace()
    {
        var points = RoundTrip([new Point(1.5, -2), new Point(0, 4)]);
        var mutable = RoundTrip([new MutablePoint { X = 1, Y = 2 }, new MutablePoint { X = 3, Y = 4 }]);

        await Assert.That(points).IsEquivalentTo(new[] { new Point(1.5, -2), new Point(0, 4) });
        await Assert.That(mutable.Select(p => (p.X, p.Y))).IsEquivalentTo(new[] { (1.0, 2.0), (3.0, 4.0) });
    }

    [Test]
    public async Task InitOnlyProperties_AreSet_AndANullOverwritesTheInitializer()
    {
        var rows = RoundTrip([new WithInitOnly { Id = 1, Label = "one", Note = null }, new WithInitOnly { Id = 2, Label = "two", Note = "n" }]);

        await Assert.That(rows.Select(r => r.Id)).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(rows.Select(r => r.Label)).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(rows[0].Note).IsNull();
        await Assert.That(rows[1].Note).IsEqualTo("n");
    }

    [Test]
    public async Task APropertyWithoutAPublicSetter_IsLeftAlone()
    {
        var rows = RoundTrip([new WithPrivateSetter { Name = "a" }]);

        // Id has a private setter and the type has a constructor that takes nothing, so only Name is read.
        await Assert.That(rows[0].Name).IsEqualTo("a");
        await Assert.That(rows[0].Id).IsEqualTo(0);
        await Assert.That(rows[0].Computed).IsEqualTo(0);
    }

    [Test]
    public async Task AConstructorTakesSomeProperties_AndSettersTakeTheRest()
    {
        var a = RoundTrip([new WithConstructorAndSetter(7) { Name = "seven" }]);
        var b = RoundTrip([new WithExtraProperty(8) { Name = "eight" }]);

        await Assert.That(a[0].Id).IsEqualTo(7);
        await Assert.That(a[0].Name).IsEqualTo("seven");
        await Assert.That(b[0].Id).IsEqualTo(8);
        await Assert.That(b[0].Name).IsEqualTo("eight");
    }

    private sealed class Unbuildable
    {
        public Unbuildable(string unrelated)
        {
            Id = unrelated.Length;
        }

        public int Id { get; }
    }

    private sealed class NothingToSet
    {
        public int Id { get; } = 1;
    }

    private sealed class Clashing
    {
        public int A { get; set; }

        [ParquetColumn("A")]
        public int B { get; set; }
    }

    private sealed class Unsupported
    {
        public int Int { get; set; }

        public TimeSpan Duration { get; set; }
    }

    [Test]
    public async Task ATypeThatCannotBeCreated_OrHasNothingToRead_IsReported()
    {
        var bytes = Serialize(Sample.Rows(2));

        await Assert.That(() => ParquetSerializer.Deserialize<Unbuildable>(new MemoryStream(bytes))).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.Deserialize<NothingToSet>(new MemoryStream(bytes))).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.Deserialize<Clashing>(new MemoryStream(bytes))).Throws<InvalidOperationException>();
        var unsupported = Assert.Throws<NotSupportedException>(() => ParquetSerializer.Deserialize<Unsupported>(new MemoryStream(bytes)));
        await Assert.That(unsupported.Message).Contains("Unsupported.Duration");
    }

    // ---- enums ----

    private enum Color
    {
        Red,
        Green,
        Blue,
    }

    [Flags]
    private enum Permission
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = 3,
    }

    private sealed class Paint
    {
        public Color Color { get; set; }

        public Color? Accent { get; set; }

        public Permission Access { get; set; }
    }

    [Test]
    public async Task Enums_AreReadByName_IncludingCombinationsOfFlags()
    {
        var rows = RoundTrip([
            new Paint { Color = Color.Blue, Accent = null, Access = Permission.Read | Permission.Write },
            new Paint { Color = Color.Red, Accent = Color.Green, Access = (Permission)4 },
            new Paint { Color = Color.Green, Accent = Color.Red, Access = Permission.None },
        ]);

        await Assert.That(rows.Select(r => r.Color)).IsEquivalentTo(new[] { Color.Blue, Color.Red, Color.Green });
        await Assert.That(rows.Select(r => r.Accent)).IsEquivalentTo(new Color?[] { null, Color.Green, Color.Red });
        await Assert.That(rows.Select(r => r.Access)).IsEquivalentTo(new[] { Permission.ReadWrite, (Permission)4, Permission.None });
    }

    private sealed class JustColor
    {
        public Color Color { get; set; }
    }

    private sealed class Texts
    {
        public string Color { get; set; } = "";
    }

    [Test]
    public async Task AnUnknownEnumName_IsReported()
    {
        var bytes = Serialize([new Texts { Color = "Red" }, new Texts { Color = "Purple" }]);

        var exception = Assert.Throws<ValueConversionException>(() => ParquetSerializer.Deserialize<JustColor>(new MemoryStream(bytes)));

        await Assert.That(exception.Message).Contains("Purple");
        await Assert.That(exception.Message).Contains("Color");
        await Assert.That(ParquetSerializer.Deserialize<Texts>(new MemoryStream(bytes)).Count).IsEqualTo(2);
    }
}
