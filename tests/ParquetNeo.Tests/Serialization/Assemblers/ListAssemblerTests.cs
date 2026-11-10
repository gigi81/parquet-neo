using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Tests.Serialization.Assemblers;

public class ListAssemblerTests
{
    private sealed class Holder
    {
        public List<int?>? Values { get; set; }

        public int Plain { get; set; }
    }

    private static AssemblerHarness File() => AssemblerHarness.Write(
    [
        new Holder { Values = [1, 2, 3] },
        new Holder { Values = null },
        new Holder { Values = [] },
        new Holder { Values = [4, null, 5] },
        new Holder { Values = Enumerable.Range(0, 40).Select(i => (int?)i).ToList() },
    ]);

    private static ListAssembler<List<int?>, int?, int> NullableElements(bool elementOptional = true, int scratchIndex = 0) =>
        new(node: 0, firstColumn: 0, columnCount: 1, "Holder.Values", elementOptional, () => new List<int?>(), toArray: false, scratchIndex, element => element, new LeafAssembler<int>(slot: 0, repetition: 1, "Holder.Values, an element,"));

    private static List<int?>?[] ReadAll<TCollection>(ListAssembler<TCollection, int?, int> list, ReadContext context)
        where TCollection : class, ICollection<int?>
    {
        var rows = new List<List<int?>?>();
        while (!context.Columns[0].AtEnd)
        {
            rows.Add(list.Read(context, out var value) ? [.. value] : null);
            context.Row++;
        }

        return [.. rows];
    }

    [Test]
    public async Task ItReadsNullListsEmptyListsAndElementsApart()
    {
        using var file = File();
        var list = NullableElements();
        var context = file.Read(list, file.Node("Values"), columnCount: 1, nodeCount: 1);

        var rows = ReadAll(list, context);

        await Assert.That(rows.Length).IsEqualTo(5);
        await Assert.That(rows[0]).IsEquivalentTo(new int?[] { 1, 2, 3 });
        await Assert.That(rows[1]).IsNull();
        await Assert.That(rows[2]).IsNotNull().And.IsEmpty();
        await Assert.That(rows[3]).IsEquivalentTo(new int?[] { 4, null, 5 });
        await Assert.That(rows[4]).IsEquivalentTo(Enumerable.Range(0, 40).Select(i => (int?)i).ToArray());
    }

    [Test]
    public async Task AListIsAsBigAsItsElements_AndNotTheOneBefore()
    {
        using var file = File();
        var list = NullableElements();
        var context = file.Read(list, file.Node("Values"), columnCount: 1, nodeCount: 1);

        list.Read(context, out var first);
        list.Read(context, out _);
        list.Read(context, out var empty);
        list.Read(context, out var third);

        await Assert.That(first.Capacity).IsEqualTo(3);
        await Assert.That(empty.Capacity).IsEqualTo(0);
        await Assert.That(third.Capacity).IsEqualTo(3);
        await Assert.That(first).IsNotSameReferenceAs(third);
        await Assert.That(first).IsEquivalentTo(new int?[] { 1, 2, 3 });
    }

    [Test]
    public async Task AnArrayIsMadeFromTheScratchToo()
    {
        using var file = File();
        var array = new ListAssembler<int?[], int?, int>(0, 0, 1, "Holder.Values", true, () => new List<int?>(), toArray: true, scratchIndex: 0, element => element, new LeafAssembler<int>(0, 1, "e"));
        var context = file.Read(array, file.Node("Values"), 1, 1);

        array.Read(context, out var first);
        array.Read(context, out _);
        array.Read(context, out var empty);
        array.Read(context, out var third);

        await Assert.That(first).IsEquivalentTo(new int?[] { 1, 2, 3 });
        await Assert.That(empty).IsEmpty();
        await Assert.That(third).IsEquivalentTo(new int?[] { 4, null, 5 });
    }

    [Test]
    public async Task ACollectionThatIsNotAListOrArray_IsCreatedAndAddedTo()
    {
        using var file = File();
        var set = new ListAssembler<HashSet<int?>, int?, int>(0, 0, 1, "Holder.Values", true, () => new HashSet<int?>(), toArray: false, scratchIndex: -1, element => element, new LeafAssembler<int>(0, 1, "e"));
        var context = file.Read(set, file.Node("Values"), 1, 1);

        var rows = ReadAll(set, context);

        await Assert.That(rows[0]).IsEquivalentTo(new int?[] { 1, 2, 3 });
        await Assert.That(rows[1]).IsNull();
        await Assert.That(rows[3]).IsEquivalentTo(new int?[] { 4, null, 5 });
        await Assert.That(context.Objects.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ANullElement_OfAListThatCannotHoldNulls_IsRefusedWithTheRow()
    {
        using var file = File();
        var list = NullableElements(elementOptional: false);
        var context = file.Read(list, file.Node("Values"), 1, 1);
        context.Row = 3;
        list.Read(context, out _);
        list.Read(context, out _);
        list.Read(context, out _);

        var exception = Assert.Throws<ValueConversionException>(() => list.Read(context, out _));

        await Assert.That(exception.Message).Contains("Holder.Values");
        await Assert.That(exception.Message).Contains("row 3");
    }

    [Test]
    public async Task ANullElement_OfAListThatCannotHoldNulls_IsRefusedByTheCollectionsToo()
    {
        using var file = File();
        var set = new ListAssembler<HashSet<int?>, int?, int>(0, 0, 1, "Holder.Values", false, () => new HashSet<int?>(), toArray: false, scratchIndex: -1, element => element, new LeafAssembler<int>(0, 1, "e"));
        var context = file.Read(set, file.Node("Values"), 1, 1);
        context.Row = 9;
        set.Read(context, out _);
        set.Read(context, out _);
        set.Read(context, out _);

        var exception = Assert.Throws<ValueConversionException>(() => set.Read(context, out _));

        await Assert.That(exception.Message).Contains("row 9");
    }

    [Test]
    public async Task AListTheFileLacks_IsNull()
    {
        using var file = File();
        var list = NullableElements();
        var context = file.Read(list, node: null, columnCount: 1, nodeCount: 1);

        await Assert.That(list.Read(context, out _)).IsFalse();
    }

    [Test]
    public async Task APropertyThatIsNotAListInTheFile_IsRefused()
    {
        using var file = File();

        var column = Assert.Throws<SchemaMismatchException>(() => NullableElements().Bind(file.Node("Plain"), new ReadBinder(1, 1)));

        await Assert.That(column.Message).Contains("column 'Plain'");
        await Assert.That(column.Message).Contains("is not a list");
    }

    [Test]
    public async Task APropertyThatIsAGroupButNotAListInTheFile_IsRefused()
    {
        using var other = AssemblerHarness.Write([new { Values = new { A = 1 } }]);

        var exception = Assert.Throws<SchemaMismatchException>(() => NullableElements().Bind(other.Node("Values"), new ReadBinder(1, 1)));

        await Assert.That(exception.Message).Contains("group 'Values'");
        await Assert.That(exception.Message).Contains("is not a list");
    }

    [Test]
    public async Task ItAddsTheScratchBeforeTheStateOfItsElement_OnlyWhenItUsesOne()
    {
        var withScratch = new List<object>();
        var without = new List<object>();

        NullableElements(scratchIndex: 0).AddState([], withScratch);
        new ListAssembler<HashSet<int?>, int?, int>(0, 0, 1, "x", true, () => new HashSet<int?>(), false, -1, e => e, new LeafAssembler<int>(0, 1, "e")).AddState([], without);

        await Assert.That(withScratch.Count).IsEqualTo(1);
        await Assert.That(withScratch[0]).IsTypeOf<ItemScratch<int?>>();
        await Assert.That(without.Count).IsEqualTo(0);
    }
}
