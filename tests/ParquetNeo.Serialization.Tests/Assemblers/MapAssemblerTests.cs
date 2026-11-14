using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Tests.Assemblers;

public class MapAssemblerTests
{
    private sealed class Holder
    {
        public Dictionary<string, int?>? Pairs { get; set; }

        public int Plain { get; set; }
    }

    private static AssemblerHarness File() => AssemblerHarness.Write(
    [
        new Holder { Pairs = new Dictionary<string, int?> { ["a"] = 1, ["b"] = null } },
        new Holder { Pairs = null },
        new Holder { Pairs = [] },
        new Holder { Pairs = new Dictionary<string, int?> { ["c"] = 3, ["d"] = 4, ["e"] = 5 } },
    ]);

    private static MapAssembler<Dictionary<string, int?>, string, int?, int> Pairs(bool valueOptional = true) =>
        new(node: 0, firstColumn: 0, columnCount: 2, "Holder.Pairs", valueOptional, () => new Dictionary<string, int?>(), value => value, new LeafAssembler<string>(0, 1, "key"), new LeafAssembler<int>(1, 1, "value"));

    [Test]
    public async Task ItReadsNullMapsEmptyMapsAndEntriesApart()
    {
        using var file = File();
        var map = Pairs();
        var context = file.Read(map, file.Node("Pairs"), columnCount: 2, nodeCount: 1);

        var rows = new List<Dictionary<string, int?>?>();
        while (!context.Columns[0].AtEnd)
        {
            rows.Add(map.Read(context, out var value) ? value : null);
        }

        await Assert.That(rows.Count).IsEqualTo(4);
        await Assert.That(rows[0]!.Select(p => $"{p.Key}={p.Value?.ToString() ?? "null"}")).IsEquivalentTo(["a=1", "b=null"]);
        await Assert.That(rows[1]).IsNull();
        await Assert.That(rows[2]).IsNotNull().And.IsEmpty();
        await Assert.That(rows[3]!.Select(p => $"{p.Key}={p.Value}")).IsEquivalentTo(["c=3", "d=4", "e=5"]);
    }

    [Test]
    public async Task ANullValue_OfAMapThatCannotHoldNulls_IsRefusedWithTheRow()
    {
        using var file = File();
        var map = Pairs(valueOptional: false);
        var context = file.Read(map, file.Node("Pairs"), 2, 1);
        context.Row = 41;

        var exception = Assert.Throws<ValueConversionException>(() => map.Read(context, out _));

        await Assert.That(exception.Message).Contains("Holder.Pairs");
        await Assert.That(exception.Message).Contains("row 41");
    }

    [Test]
    public async Task AMapTheFileLacks_IsNull()
    {
        using var file = File();
        var map = Pairs();
        var context = file.Read(map, node: null, columnCount: 2, nodeCount: 1);

        await Assert.That(map.Read(context, out _)).IsFalse();
    }

    [Test]
    public async Task APropertyThatIsNotAMapInTheFile_IsRefused()
    {
        using var file = File();

        var exception = Assert.Throws<SchemaMismatchException>(() => Pairs().Bind(file.Node("Plain"), new ReadBinder(2, 1)));

        await Assert.That(exception.Message).Contains("column 'Plain'");
        await Assert.That(exception.Message).Contains("is not a map");
    }

    [Test]
    public async Task AMapWithKeysButNoValues_IsRefused()
    {
        var keys = new GroupNode(
            "Pairs",
            FieldRepetitionType.Optional,
            [new GroupNode("key_value", FieldRepetitionType.Repeated, [new PrimitiveNode("key", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)])],
            LogicalType.Map);
        var schema = new ParquetSchema([keys]);

        var exception = Assert.Throws<SchemaMismatchException>(() => Pairs().Bind(schema.Root.Children[0], new ReadBinder(2, 1)));

        await Assert.That(exception.Message).Contains("has keys but no values");
    }

    [Test]
    public async Task ItAddsTheStateOfItsKeysAndItsValues()
    {
        var columns = new List<ParquetNeo.Serialization.Columns.ILeafColumn>();

        Pairs().AddState(columns, []);

        await Assert.That(columns.Count).IsEqualTo(2);
    }
}
