using ParquetNeo.Metadata;
using ParquetNeo.Nested;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

public class NestedSchemaTests
{
    private static PrimitiveNode Leaf(string name, FieldRepetitionType repetition = FieldRepetitionType.Required, PhysicalType type = PhysicalType.Int32) =>
        new(name, type, repetition);

    // ---- builders ----

    [Test]
    public async Task List_BuildsTheStandardLayout()
    {
        var list = NestedSchema.List("scores", FieldRepetitionType.Optional, Leaf("element", FieldRepetitionType.Optional));

        await Assert.That(list.LogicalType).IsEqualTo(LogicalType.List);
        await Assert.That(list.ConvertedType).IsEqualTo(ConvertedType.List);
        await Assert.That(list.Repetition).IsEqualTo(FieldRepetitionType.Optional);
        var repeated = (GroupNode)list.Children.Single();
        await Assert.That(repeated.Name).IsEqualTo("list");
        await Assert.That(repeated.Repetition).IsEqualTo(FieldRepetitionType.Repeated);
        await Assert.That(repeated.Children.Single().Name).IsEqualTo("element");
    }

    [Test]
    public async Task List_AcceptsOtherNames_AndFieldIds()
    {
        var list = NestedSchema.List("l", FieldRepetitionType.Required, Leaf("item"), repeatedName: "bag", fieldId: 7);

        await Assert.That(((GroupNode)list.Children.Single()).Name).IsEqualTo("bag");
        await Assert.That(list.FieldId).IsEqualTo(7);
    }

    [Test]
    public async Task List_RejectsRepeatedParts()
    {
        await Assert.That(() => NestedSchema.List("l", FieldRepetitionType.Repeated, Leaf("element"))).Throws<ArgumentException>();
        await Assert.That(() => NestedSchema.List("l", FieldRepetitionType.Optional, Leaf("element", FieldRepetitionType.Repeated))).Throws<ArgumentException>();
        await Assert.That(() => NestedSchema.List("l", FieldRepetitionType.Optional, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Map_BuildsTheStandardLayout()
    {
        var map = NestedSchema.Map("attributes", FieldRepetitionType.Optional, Leaf("key", type: PhysicalType.ByteArray), Leaf("value", FieldRepetitionType.Optional));

        await Assert.That(map.LogicalType).IsEqualTo(LogicalType.Map);
        await Assert.That(map.ConvertedType).IsEqualTo(ConvertedType.Map);
        var keyValue = (GroupNode)map.Children.Single();
        await Assert.That(keyValue.Name).IsEqualTo("key_value");
        await Assert.That(keyValue.Repetition).IsEqualTo(FieldRepetitionType.Repeated);
        await Assert.That(keyValue.Children.Select(c => c.Name)).IsEquivalentTo(new[] { "key", "value" });
    }

    [Test]
    public async Task Map_RequiresARequiredKey_AndNoRepeatedParts()
    {
        await Assert.That(() => NestedSchema.Map("m", FieldRepetitionType.Optional, Leaf("key", FieldRepetitionType.Optional), Leaf("value"))).Throws<ArgumentException>();
        await Assert.That(() => NestedSchema.Map("m", FieldRepetitionType.Repeated, Leaf("key"), Leaf("value"))).Throws<ArgumentException>();
        await Assert.That(() => NestedSchema.Map("m", FieldRepetitionType.Optional, Leaf("key"), Leaf("value", FieldRepetitionType.Repeated))).Throws<ArgumentException>();
    }

    // ---- list layouts: the examples of the specification ----

    [Test]
    public async Task AStandardList_IsThreeLevel_AndItsElementCanBeNull()
    {
        var list = NestedSchema.List("my_list", FieldRepetitionType.Optional, Leaf("element", FieldRepetitionType.Optional));
        _ = new ParquetSchema([list]);

        var layout = NestedSchema.GetListLayout(list)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.ThreeLevel);
        await Assert.That(layout.Container).IsSameReferenceAs(list);
        await Assert.That(layout.Repeated.Name).IsEqualTo("list");
        await Assert.That(layout.Element.Name).IsEqualTo("element");
        await Assert.That(layout.ElementIsNullable).IsTrue();
        await Assert.That(layout.ListIsNullable).IsTrue();
    }

    [Test]
    public async Task ARequiredListOfRequiredElements_IsNotNullable()
    {
        var list = NestedSchema.List("my_list", FieldRepetitionType.Required, Leaf("element"));

        var layout = NestedSchema.GetListLayout(list)!;

        await Assert.That(layout.ElementIsNullable).IsFalse();
        await Assert.That(layout.ListIsNullable).IsFalse();
    }

    [Test]
    public async Task ARepeatedPrimitive_InAListGroup_IsTheElement()
    {
        // optional group my_list (LIST) { repeated int32 element; }
        var schema = LegacyListFile.Schema();
        var layout = NestedSchema.GetListLayout(schema.Root.FindChild("two_level")!)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.RepeatedPrimitive);
        await Assert.That(layout.Element).IsSameReferenceAs(layout.Repeated);
        await Assert.That(layout.ElementIsNullable).IsFalse();
    }

    [Test]
    public async Task ARepeatedGroupWithSeveralFields_IsTheElement()
    {
        // optional group my_list (LIST) { repeated group element { required binary str; required int32 num; } }
        var schema = LegacyListFile.Schema();
        var layout = NestedSchema.GetListLayout(schema.Root.FindChild("struct_element")!)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.RepeatedStruct);
        await Assert.That(layout.Element).IsSameReferenceAs(layout.Repeated);
    }

    [Test]
    public async Task ARepeatedGroupNamedTupleOrArray_WithOneField_IsTheElement()
    {
        var schema = LegacyListFile.Schema();

        var tuple = NestedSchema.GetListLayout(schema.Root.FindChild("tuple")!)!;
        var array = NestedSchema.GetListLayout(schema.Root.FindChild("array")!)!;

        await Assert.That(tuple.Style).IsEqualTo(ListStyle.RepeatedTuple);
        await Assert.That(tuple.Element).IsSameReferenceAs(tuple.Repeated);
        await Assert.That(array.Style).IsEqualTo(ListStyle.RepeatedTuple);
        await Assert.That(array.Element).IsSameReferenceAs(array.Repeated);
    }

    [Test]
    public async Task ARepeatedGroupWithOneOtherlyNamedField_WrapsTheElement()
    {
        // optional group my_list (LIST) { repeated group element { required binary str; } }: the list holds the strings
        var list = new GroupNode("my_list", FieldRepetitionType.Optional,
            [new GroupNode("element", FieldRepetitionType.Repeated, [Leaf("str", type: PhysicalType.ByteArray)])], LogicalType.List);
        _ = new ParquetSchema([list]);

        var layout = NestedSchema.GetListLayout(list)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.ThreeLevel);
        await Assert.That(layout.Element.Name).IsEqualTo("str");
    }

    [Test]
    public async Task AListOfLists_UsesTheArrayRuleForItsOuterList()
    {
        // optional group array_of_arrays (LIST) { repeated group array (LIST) { repeated int32 array; } }
        var outer = new GroupNode("array_of_arrays", FieldRepetitionType.Optional,
            [new GroupNode("array", FieldRepetitionType.Repeated, [Leaf("array", FieldRepetitionType.Repeated)], LogicalType.List)], LogicalType.List);
        _ = new ParquetSchema([outer]);

        var layout = NestedSchema.GetListLayout(outer)!;
        var inner = NestedSchema.GetListLayout(layout.Element)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.RepeatedTuple);
        await Assert.That(inner.Style).IsEqualTo(ListStyle.RepeatedPrimitive);
    }

    [Test]
    public async Task ARepeatedFieldOutsideAnyListGroup_IsAnImplicitList()
    {
        var schema = LegacyListFile.Schema();
        var implicitField = schema.Root.FindChild("implicit")!;

        var layout = NestedSchema.GetListLayout(implicitField)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.Implicit);
        await Assert.That(layout.Element).IsSameReferenceAs(implicitField);
        await Assert.That(layout.ElementIsNullable).IsFalse();
        await Assert.That(layout.ListIsNullable).IsFalse();
    }

    [Test]
    public async Task ThingsThatAreNotLists_HaveNoLayout()
    {
        var plain = Leaf("x", FieldRepetitionType.Optional);
        var struct1 = new GroupNode("s", FieldRepetitionType.Optional, [Leaf("a")]);
        var twoRepeated = new GroupNode("l", FieldRepetitionType.Optional, [Leaf("a", FieldRepetitionType.Repeated), Leaf("b", FieldRepetitionType.Repeated)], LogicalType.List);
        var notRepeated = new GroupNode("l", FieldRepetitionType.Optional, [Leaf("a")], LogicalType.List);
        var empty = new GroupNode("l", FieldRepetitionType.Optional, [], LogicalType.List);

        await Assert.That(NestedSchema.GetListLayout(plain)).IsNull();
        await Assert.That(NestedSchema.GetListLayout(struct1)).IsNull();
        await Assert.That(NestedSchema.GetListLayout(twoRepeated)).IsNull();
        await Assert.That(NestedSchema.GetListLayout(notRepeated)).IsNull();
        await Assert.That(NestedSchema.GetListLayout(empty)).IsNull();
    }

    [Test]
    public async Task AListAnnotatedOnlyByTheLegacyConvertedType_IsRecognizedAfterReading()
    {
        var elements = new List<SchemaElement>
        {
            new() { Name = "schema", NumChildren = 1 },
            new() { Name = "xs", RepetitionType = FieldRepetitionType.Optional, NumChildren = 1, ConvertedType = ConvertedType.List },
            new() { Name = "list", RepetitionType = FieldRepetitionType.Repeated, NumChildren = 1 },
            new() { Name = "element", RepetitionType = FieldRepetitionType.Optional, Type = PhysicalType.Int32 },
        };

        var schema = ParquetSchema.FromElements(elements);
        var layout = NestedSchema.GetListLayout(schema.Root.FindChild("xs")!)!;

        await Assert.That(layout.Style).IsEqualTo(ListStyle.ThreeLevel);
        await Assert.That(layout.Element.Name).IsEqualTo("element");
    }

    // ---- map layouts ----

    [Test]
    public async Task AStandardMap_HasKeyAndValue()
    {
        var map = NestedSchema.Map("m", FieldRepetitionType.Optional, Leaf("key", type: PhysicalType.ByteArray), Leaf("value", FieldRepetitionType.Optional));
        _ = new ParquetSchema([map]);

        var layout = NestedSchema.GetMapLayout(map)!;

        await Assert.That(layout.Container).IsSameReferenceAs(map);
        await Assert.That(layout.KeyValue.Name).IsEqualTo("key_value");
        await Assert.That(layout.Key.Name).IsEqualTo("key");
        await Assert.That(layout.Value!.Name).IsEqualTo("value");
    }

    [Test]
    public async Task AMapWithoutValues_IsAKeyOnlyMap()
    {
        var map = new GroupNode("m", FieldRepetitionType.Optional,
            [new GroupNode("map", FieldRepetitionType.Repeated, [Leaf("key", type: PhysicalType.ByteArray)])], LogicalType.Map);

        var layout = NestedSchema.GetMapLayout(map)!;

        await Assert.That(layout.Value).IsNull();
        await Assert.That(layout.Key.Name).IsEqualTo("key");
    }

    [Test]
    public async Task TheLegacyMapKeyValueAnnotation_IsRecognized()
    {
        var map = new GroupNode("m", FieldRepetitionType.Optional,
            [new GroupNode("map", FieldRepetitionType.Repeated, [Leaf("key"), Leaf("value", FieldRepetitionType.Optional)])],
            logicalType: null, convertedType: ConvertedType.MapKeyValue, fieldId: null, validate: false);

        var layout = NestedSchema.GetMapLayout(map)!;

        await Assert.That(layout.Key.Name).IsEqualTo("key");
        await Assert.That(layout.Value!.Name).IsEqualTo("value");
    }

    [Test]
    public async Task ThingsThatAreNotMaps_HaveNoLayout()
    {
        GroupNode WithKeyValue(params SchemaNode[] fields) =>
            new("m", FieldRepetitionType.Optional, [new GroupNode("key_value", FieldRepetitionType.Repeated, fields)], LogicalType.Map);

        await Assert.That(NestedSchema.GetMapLayout(WithKeyValue(Leaf("key", FieldRepetitionType.Optional), Leaf("value")))).IsNull(); // optional key
        await Assert.That(NestedSchema.GetMapLayout(WithKeyValue(Leaf("key"), Leaf("value"), Leaf("extra")))).IsNull();
        await Assert.That(NestedSchema.GetMapLayout(WithKeyValue())).IsNull();
        await Assert.That(NestedSchema.GetMapLayout(new GroupNode("m", FieldRepetitionType.Optional, [Leaf("a")], LogicalType.Map))).IsNull();
        await Assert.That(NestedSchema.GetMapLayout(new GroupNode("s", FieldRepetitionType.Optional, [Leaf("a")]))).IsNull(); // not annotated
        await Assert.That(NestedSchema.GetMapLayout(Leaf("x"))).IsNull();
    }

    // ---- the legacy list layouts in a file ----

    [Test]
    public async Task TheLegacyListLayouts_RoundTrip_WithTheStructureTheLayoutPromises()
    {
        var rows = LegacyListFile.Rows();
        var bytes = NestedFile.Write(LegacyListFile.Schema(), rows, new ParquetWriterOptions { DataPageSize = 64 });
        TestParquetFile.Parse(bytes).AssertStructure();
        using var reader = ReadHelper.Open(bytes);
        var expected = ReferenceShredder.Shred(reader.Schema, rows);

        await Assert.That(reader.Schema.Columns.Select(c => string.Join('.', c.Path))).IsEquivalentTo(LegacyListFile.ColumnNames);
        for (int c = 0; c < expected.Length; c++)
        {
            var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, c, 5);
            string name = LegacyListFile.ColumnNames[c];
            await Assert.That(definition).IsEquivalentTo(expected[c].Definition).Because(name);
            await Assert.That(repetition).IsEquivalentTo(expected[c].Repetition).Because(name);
            await Assert.That(values.Select(NestedReaderTests.Describe).SequenceEqual(expected[c].Values.Select(NestedReaderTests.Describe))).IsTrue().Because(name);

            var structure = ColumnStructure.FromLevels(reader.Schema.Columns[c], definition, repetition);
            await Assert.That(structure.RowCount).IsEqualTo(LegacyListFile.RowCount).Because(name);
        }
    }

    [Test]
    public async Task TheGoldenLegacyListFile_MatchesByteForByte()
    {
        var actual = NestedFile.Write(LegacyListFile.Schema(), LegacyListFile.Rows(), new ParquetWriterOptions { DataPageSize = 128, CreatedBy = "ParquetNeo golden" });

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-legacy-lists.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-legacy-lists.parquet"))));
    }
}
