using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Tests.Serialization.Assemblers;

public class LeafAssemblerTests
{
    private sealed class Holder
    {
        public int Id { get; set; }

        public int? Score { get; set; }

        public string? Name { get; set; }

        public List<int> Values { get; set; } = [];
    }

    private static AssemblerHarness File() => AssemblerHarness.Write(
    [
        new Holder { Id = 1, Score = 10, Name = "a", Values = [1, 2] },
        new Holder { Id = 2, Score = null, Name = null },
        new Holder { Id = 3, Score = 30, Name = "c", Values = [3] },
    ]);

    [Test]
    public async Task ItTakesTheValuesOfItsColumn_InOrder()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Id");
        var context = file.Read(leaf, file.Node("Id"), columnCount: 1, nodeCount: 0);

        var values = new List<int>();
        while (!context.Columns[0].AtEnd)
        {
            await Assert.That(leaf.Read(context, out int value)).IsTrue();
            values.Add(value);
        }

        await Assert.That(values).IsEquivalentTo([1, 2, 3]);
    }

    [Test]
    public async Task ANullEntry_IsNotAValue()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Score");
        var context = file.Read(leaf, file.Node("Score"), columnCount: 1, nodeCount: 0);

        var present = new List<bool>();
        while (!context.Columns[0].AtEnd)
        {
            present.Add(leaf.Read(context, out _));
        }

        await Assert.That(present).IsEquivalentTo([true, false, true]);
    }

    [Test]
    public async Task AColumnTheFileLacks_HasOnlyNulls_HoweverManyAreTaken()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Other");
        var context = file.Read(leaf, node: null, columnCount: 1, nodeCount: 0);

        for (int i = 0; i < 10; i++)
        {
            await Assert.That(leaf.Read(context, out _)).IsFalse();
        }
    }

    [Test]
    public async Task ItIsBoundToTheColumnOfTheFile_OrToNothing()
    {
        using var file = File();
        var leaf = new LeafAssembler<string>(slot: 0, repetition: 0, "Holder.Name");

        var binder = new ReadBinder(columnCount: 1, nodeCount: 0);
        leaf.Bind(file.Node("Name"), binder);
        await Assert.That(binder.ToPlan().FileColumns).IsEquivalentTo([file.Schema.Columns.ToList().IndexOf(file.Column("Name"))]);

        var unbound = new ReadBinder(columnCount: 1, nodeCount: 0);
        leaf.Bind(null, unbound);
        await Assert.That(unbound.ToPlan().FileColumns).IsEquivalentTo([-1]);
    }

    [Test]
    public async Task ARepeatedColumn_CannotBeReadAsASingleValue()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Values");

        var exception = Assert.Throws<SchemaMismatchException>(() => leaf.Bind(file.Column("Values.list.element"), new ReadBinder(1, 0)));

        await Assert.That(exception.Message).Contains("Values.list.element");
        await Assert.That(exception.Message).Contains("repeated");
    }

    [Test]
    public async Task ASingleColumn_CannotBeReadAsTheElementsOfACollection()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 2, "Holder.Values, an element,");

        var exception = Assert.Throws<SchemaMismatchException>(() => leaf.Bind(file.Column("Values.list.element"), new ReadBinder(1, 0)));

        await Assert.That(exception.Message).Contains("repeated 1 times, but the property is in 2 collections");
    }

    [Test]
    public async Task AGroup_CannotBeReadAsAValue()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Values");

        var exception = Assert.Throws<SchemaMismatchException>(() => leaf.Bind(file.Node("Values"), new ReadBinder(1, 0)));

        await Assert.That(exception.Message).Contains("is a group, but the property is a simple type");
    }

    [Test]
    public async Task AColumnOfAnotherType_IsRefusedWithTheReason()
    {
        using var file = File();
        var leaf = new LeafAssembler<int>(slot: 0, repetition: 0, "Holder.Name");

        var exception = Assert.Throws<SchemaMismatchException>(() => leaf.Bind(file.Node("Name"), new ReadBinder(1, 0)));

        await Assert.That(exception.Message).Contains("The column 'Name' of the file cannot be read into Holder.Name");
    }

    [Test]
    public async Task ItAddsOneColumnToTheState_AndNoObjects()
    {
        var columns = new List<ParquetNeo.Serialization.Columns.ILeafColumn>();
        var objects = new List<object>();

        new LeafAssembler<double>(slot: 4, repetition: 0, "x").AddState(columns, objects);

        await Assert.That(columns.Count).IsEqualTo(1);
        await Assert.That(objects.Count).IsEqualTo(0);
    }
}
