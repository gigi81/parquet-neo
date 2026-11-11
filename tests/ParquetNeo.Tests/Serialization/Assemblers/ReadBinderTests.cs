using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Tests.Serialization.Assemblers;

public class ReadBinderTests
{
    private static GroupNode Group(params string[] names) =>
        new("root", FieldRepetitionType.Required, names.Select(name => (SchemaNode)new PrimitiveNode(name, PhysicalType.Int32, FieldRepetitionType.Required)).ToArray());

    [Test]
    public async Task Find_PrefersTheExactName()
    {
        var group = Group("id", "Id", "ID");

        await Assert.That(ReadBinder.Find(group, "Id")?.Name).IsEqualTo("Id");
        await Assert.That(ReadBinder.Find(group, "id")?.Name).IsEqualTo("id");
    }

    [Test]
    public async Task Find_TakesTheOneThatDiffersOnlyByCase()
    {
        var group = Group("first_name", "Age");

        await Assert.That(ReadBinder.Find(group, "First_Name")?.Name).IsEqualTo("first_name");
        await Assert.That(ReadBinder.Find(group, "age")?.Name).IsEqualTo("Age");
    }

    [Test]
    public async Task Find_GivesUp_WhenSeveralDifferOnlyByCase_OrNoneDoes()
    {
        var group = Group("name", "NAME");

        await Assert.That(ReadBinder.Find(group, "Name")).IsNull();
        await Assert.That(ReadBinder.Find(group, "missing")).IsNull();
    }

    [Test]
    public async Task ThePlan_HoldsTheFileColumnOfEverySlot_AndMinusOneForTheOnesThatAreNotBound()
    {
        var binder = new ReadBinder(columnCount: 3, nodeCount: 0);
        binder.BindColumn(slot: 0, fileColumn: 7);
        binder.BindColumn(slot: 2, fileColumn: 1);

        var plan = binder.ToPlan();

        await Assert.That(plan.FileColumns).IsEquivalentTo([7, -1, 1]);
    }

    [Test]
    public async Task ANodesController_IsTheFirstOfItsColumnsThatTheFileHas()
    {
        var binder = new ReadBinder(columnCount: 4, nodeCount: 2);
        binder.BindColumn(slot: 2, fileColumn: 5);
        binder.BindColumn(slot: 3, fileColumn: 6);

        binder.BindNode(node: 0, firstColumn: 1, columnCount: 3, presentDefinition: 2, elementsDefinition: 3, repetitionLevel: 1);
        binder.BindNode(node: 1, firstColumn: 0, columnCount: 2, presentDefinition: 0, elementsDefinition: 0, repetitionLevel: 0);
        var nodes = binder.ToPlan().Nodes;

        await Assert.That(nodes[0]).IsEqualTo(new NodeBinding(2, 2, 3, 1));
        await Assert.That(nodes[1].Controller).IsEqualTo(-1);
    }

    [Test]
    public async Task ANodeThatWasNeverBound_HasNoController()
    {
        var binder = new ReadBinder(columnCount: 1, nodeCount: 1);

        await Assert.That(binder.ToPlan().Nodes[0].Controller).IsEqualTo(-1);
    }

    [Test]
    public async Task Mismatch_IsASchemaMismatchExceptionWithTheMessage()
    {
        var exception = ReadBinder.Mismatch("the column does not fit");

        await Assert.That(exception).IsTypeOf<SchemaMismatchException>();
        await Assert.That(exception.Message).IsEqualTo("the column does not fit");
    }
}
