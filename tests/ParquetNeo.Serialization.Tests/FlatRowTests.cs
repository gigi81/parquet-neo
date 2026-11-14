using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;

namespace ParquetNeo.Serialization.Tests;

/// <summary>Rows whose properties are all single columns are shredded by one compiled function; these check it does what the general path does.</summary>
public class FlatRowTests
{
    private sealed class Flat
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string? Note { get; set; }

        public double? Amount { get; set; }

        public int? Count { get; set; }

        public bool Shipped { get; set; }

        public DayOfWeek Day { get; set; }
    }

    private sealed class Wrapped
    {
        public Flat? Inner { get; set; }
    }

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    private static List<TRow> Deserialize<TRow>(byte[] bytes)
        where TRow : class, new() => ParquetSerializer.Deserialize<TRow>(new MemoryStream(bytes)).ToList();

    private static Flat[] Rows() =>
    [
        new() { Id = 1, Name = "a", Note = "x", Amount = 1.5, Count = 3, Shipped = true, Day = DayOfWeek.Monday },
        new() { Id = 2, Name = "b", Note = null, Amount = null, Count = null, Shipped = false, Day = DayOfWeek.Sunday },
        new() { Id = 3, Name = "", Note = "", Amount = 0, Count = 0, Shipped = true, Day = DayOfWeek.Saturday },
    ];

    [Test]
    public async Task ValueNullableAndReferenceProperties_RoundTrip()
    {
        var read = Deserialize<Flat>(Serialize(Rows()));

        await Assert.That(read.Select(r => r.Id)).IsEquivalentTo([1L, 2L, 3L]);
        await Assert.That(read.Select(r => r.Name)).IsEquivalentTo(["a", "b", ""]);
        await Assert.That(read.Select(r => r.Note)).IsEquivalentTo(new string?[] { "x", null, "" });
        await Assert.That(read.Select(r => r.Amount)).IsEquivalentTo(new double?[] { 1.5, null, 0 });
        await Assert.That(read.Select(r => r.Count)).IsEquivalentTo(new int?[] { 3, null, 0 });
        await Assert.That(read.Select(r => r.Shipped)).IsEquivalentTo([true, false, true]);
        await Assert.That(read.Select(r => r.Day)).IsEquivalentTo([DayOfWeek.Monday, DayOfWeek.Sunday, DayOfWeek.Saturday]);
    }

    [Test]
    public async Task AFlatRow_WritesTheSameColumnsAsTheSameRowInsideAGroup()
    {
        var flat = Deserialize<Flat>(Serialize(Rows()));
        var wrapped = Deserialize<Wrapped>(Serialize(Rows().Select(r => new Wrapped { Inner = r })));

        await Assert.That(wrapped.Select(w => w.Inner!.Id)).IsEquivalentTo(flat.Select(r => r.Id));
        await Assert.That(wrapped.Select(w => w.Inner!.Note)).IsEquivalentTo(flat.Select(r => r.Note));
        await Assert.That(wrapped.Select(w => w.Inner!.Amount)).IsEquivalentTo(flat.Select(r => r.Amount));
        await Assert.That(wrapped.Select(w => w.Inner!.Day)).IsEquivalentTo(flat.Select(r => r.Day));
    }

    [Test]
    public async Task ANullInARequiredColumn_IsRefused_NamingTheProperty()
    {
        var rows = new[] { new Flat { Id = 1, Name = "a" }, new Flat { Id = 2, Name = "b" }, new Flat { Id = 3, Name = null! } };

        var exception = Assert.Throws<ArgumentException>(() => Serialize(rows));

        await Assert.That(exception.Message).Contains("Flat.Name");
        await Assert.That(exception.Message).Contains("column is required");
        await Assert.That(exception.Message).Contains("row 2");
    }

    private sealed class Narrow
    {
        public long Id { get; set; }

        public string? Note { get; set; } = "unset";

        public double? Amount { get; set; } = -1;
    }

    [Test]
    public async Task AnOptionalPropertyTheFileLacks_IsNull_AndTheOthersAreRead()
    {
        var read = Deserialize<Narrow>(Serialize(Rows()));
        var withoutNote = Deserialize<OnlyId>(Serialize(Rows()));

        await Assert.That(read.Select(r => r.Id)).IsEquivalentTo([1L, 2L, 3L]);
        await Assert.That(read.Select(r => r.Note)).IsEquivalentTo(new string?[] { "x", null, "" });
        await Assert.That(withoutNote.Select(r => r.Id)).IsEquivalentTo([1L, 2L, 3L]);
        await Assert.That(withoutNote.All(r => r.Missing is null && r.MissingNumber is null)).IsTrue();
    }

    private sealed class OnlyId
    {
        public long Id { get; set; }

        public string? Missing { get; set; } = "unset";

        public int? MissingNumber { get; set; } = 7;
    }

    private sealed class RequiredNote
    {
        public long Id { get; set; }

        public string Note { get; set; } = "";
    }

    [Test]
    public async Task ANullInARequiredPropertyOfTheRowType_NamesTheRowOfTheFile()
    {
        var exception = Assert.Throws<ValueConversionException>(() => Deserialize<RequiredNote>(Serialize(Rows())));

        await Assert.That(exception.Message).Contains("RequiredNote.Note");
        await Assert.That(exception.Message).Contains("row 1");
    }
}
