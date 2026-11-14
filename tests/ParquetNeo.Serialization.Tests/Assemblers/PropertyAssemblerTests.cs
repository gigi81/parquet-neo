using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Tests.Assemblers;

public class PropertyAssemblerTests
{
    private sealed class Narrow
    {
        public int Id { get; set; }
    }

    private sealed class Wide
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public int? Age { get; set; }

        public string? Nickname { get; set; }
    }

    /// <summary>A record is built by its constructor, so its properties are read into slots.</summary>
    private sealed record Person(string Name, int? Age);

    private sealed class Capitalized
    {
        public int ID { get; set; }

        public string? name { get; set; }
    }

    [Test]
    public async Task ARequiredPropertyTheFileLacks_IsRefusedWithTheColumnsItHas()
    {
        using var file = AssemblerHarness.Write([new Narrow { Id = 1 }]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Wide>();

        var exception = Assert.Throws<SchemaMismatchException>(() => file.Read(builder, row));

        await Assert.That(exception.Message).Contains("has no column named 'Name'");
        await Assert.That(exception.Message).Contains("which cannot be null");
        await Assert.That(exception.Message).Contains("'Id'");
    }

    [Test]
    public async Task AnOptionalPropertyTheFileLacks_IsFine_AndIsNull()
    {
        using var file = AssemblerHarness.Write([new Narrow { Id = 7 }]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Capitalized>();
        var context = file.Read(builder, row);

        var read = row.ReadRow(context);

        await Assert.That(read.ID).IsEqualTo(7);
        await Assert.That(read.name).IsNull();
    }

    [Test]
    public async Task AProperty_MatchesAColumnThatDiffersOnlyByCase()
    {
        using var file = AssemblerHarness.Write([new Wide { Id = 5, Name = "Ada" }]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Capitalized>();

        var read = row.ReadRow(file.Read(builder, row));

        await Assert.That(read.ID).IsEqualTo(5);
        await Assert.That(read.name).IsEqualTo("Ada");
    }

    [Test]
    public async Task ThePropertiesOfARecord_AreReadIntoTheSlotsOfItsConstructor()
    {
        using var file = AssemblerHarness.Write([new Person("Ada", 36), new Person("Alan", null)]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Person>();
        var context = file.Read(builder, row);

        var first = row.ReadRow(context);
        var second = row.ReadRow(context);

        await Assert.That(first).IsEqualTo(new Person("Ada", 36));
        await Assert.That(second).IsEqualTo(new Person("Alan", null));
        await Assert.That(context.Row).IsEqualTo(2);
    }

    private sealed record Required(string Name);

    private sealed record Optional(string? Name);

    [Test]
    public async Task ANullForARequiredSlot_IsRefusedWithTheRow()
    {
        using var file = AssemblerHarness.Write([new Optional("Ada"), new Optional(null)]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<Required>();
        var context = file.Read(builder, row);
        row.ReadRow(context);

        var exception = Assert.Throws<ValueConversionException>(() => row.ReadRow(context));

        await Assert.That(exception.Message).Contains("Required.Name");
        await Assert.That(exception.Message).Contains("row 1");
    }

    [Test]
    public async Task ANullForARequiredPropertyOfAnObject_IsRefusedWithTheRow()
    {
        using var file = AssemblerHarness.Write([new Optional("Ada"), new Optional("Alan"), new Optional(null)]);
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<RequiredByProperty>();
        var context = file.Read(builder, row);
        row.ReadRow(context);
        row.ReadRow(context);

        var exception = Assert.Throws<ValueConversionException>(() => row.ReadRow(context));

        await Assert.That(exception.Message).Contains("RequiredByProperty.Name");
        await Assert.That(exception.Message).Contains("row 2");
    }

    private sealed class RequiredByProperty
    {
        public string Name { get; set; } = "";
    }
}
