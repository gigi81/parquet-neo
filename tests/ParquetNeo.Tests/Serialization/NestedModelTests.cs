using System.Collections.Immutable;
using System.Text;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;
using ParquetNeo.Tests.Core.Nested;

namespace ParquetNeo.Tests.Serialization;

public class NestedModelTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    /// <summary>The levels and values of the column with the given path in the first row group.</summary>
    private static (int[] Definition, int[] Repetition, object[] Values) Entries(byte[] bytes, string path, int rowGroup = 0)
    {
        using var reader = ReadHelper.Open(bytes);
        var column = reader.Schema.FindColumn(path.Split('.'))!;
        var (definition, repetition, values) = ReadHelper.ReadEntries(reader, rowGroup, column.ColumnIndex, 64);
        return (definition, repetition, values.Select(v => v is byte[] text && column.LogicalType?.Kind is LogicalTypeKind.String or LogicalTypeKind.Enum ? Encoding.UTF8.GetString(text) : v).ToArray());
    }

    // ---- collections ----

    private sealed class Collections
    {
        public int[] Array { get; set; } = [];

        public IEnumerable<int> Lazy { get; set; } = [];

        public IReadOnlyList<string?> ReadOnly { get; set; } = [];

        public HashSet<long> Set { get; set; } = [];

        public IList<double> Interface { get; set; } = [];

        public ICollection<bool> Bits { get; set; } = [];

        public ImmutableList<int> Immutable { get; set; } = [];

        public int[]? Optional { get; set; }
    }

    private static IEnumerable<int> Count(int n)
    {
        for (int i = 0; i < n; i++)
        {
            yield return i;
        }
    }

    [Test]
    public async Task EveryKindOfCollection_IsAList()
    {
        var rows = new[]
        {
            new Collections { Array = [1, 2], Lazy = Count(3), ReadOnly = new List<string?> { "a", null }, Set = [5L], Interface = new List<double> { 1.5 }, Bits = new[] { true, false }, Immutable = [7, 8], Optional = [9] },
            new Collections { Optional = null },
            new Collections { Array = [3], Lazy = Count(0), ReadOnly = new[] { "z" }, Set = [6L, 7L], Interface = new[] { 2.5, 3.5 }, Bits = new List<bool> { true }, Immutable = ImmutableList.Create(1), Optional = [] },
        };

        var bytes = Serialize(rows);

        var array = Entries(bytes, "Array.list.element");
        await Assert.That(array.Definition).IsEquivalentTo(new[] { 1, 1, 0, 1 });
        await Assert.That(array.Repetition).IsEquivalentTo(new[] { 0, 1, 0, 0 });
        await Assert.That(array.Values).IsEquivalentTo(new object[] { 1, 2, 3 });
        await Assert.That(Entries(bytes, "Lazy.list.element").Values).IsEquivalentTo(new object[] { 0, 1, 2 });
        var readOnly = Entries(bytes, "ReadOnly.list.element");
        await Assert.That(readOnly.Definition).IsEquivalentTo(new[] { 2, 1, 0, 2 }); // "a", null, (empty list), "z"
        await Assert.That(readOnly.Values).IsEquivalentTo(new object[] { "a", "z" });
        await Assert.That(Entries(bytes, "Set.list.element").Values).IsEquivalentTo(new object[] { 5L, 6L, 7L });
        await Assert.That(Entries(bytes, "Interface.list.element").Values).IsEquivalentTo(new object[] { 1.5, 2.5, 3.5 });
        await Assert.That(Entries(bytes, "Bits.list.element").Values).IsEquivalentTo(new object[] { true, false, true });
        await Assert.That(Entries(bytes, "Immutable.list.element").Values).IsEquivalentTo(new object[] { 7, 8, 1 });
        var optional = Entries(bytes, "Optional.list.element");
        await Assert.That(optional.Definition).IsEquivalentTo(new[] { 2, 0, 1 }); // [9], null, []
    }

    [Test]
    public async Task ARequiredAndAnOptionalList_GetTheirOwnRepetitions()
    {
        var schema = ParquetSerializer.GetSchema<Collections>();
        var byName = schema.Root.Children.ToDictionary(c => c.Name);

        await Assert.That(byName["Array"].Repetition).IsEqualTo(FieldRepetitionType.Required);
        await Assert.That(byName["Optional"].Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(byName["Array"].LogicalType).IsEqualTo(LogicalType.List);
        var element = schema.FindColumn(["ReadOnly", "list", "element"])!;
        await Assert.That(element.Repetition).IsEqualTo(FieldRepetitionType.Optional); // IReadOnlyList<string?>
        await Assert.That(schema.FindColumn(["Array", "list", "element"])!.Repetition).IsEqualTo(FieldRepetitionType.Required);
    }

    // ---- dictionaries ----

    private sealed class Maps
    {
        public Dictionary<string, List<int>> Lists { get; set; } = [];

        public IDictionary<string, int> Simple { get; set; } = new Dictionary<string, int>();

        public IReadOnlyDictionary<int, string?> Texts { get; set; } = new Dictionary<int, string?>();

        public SortedDictionary<Guid, long?> Sorted { get; set; } = [];

        public List<Dictionary<string, int>> Each { get; set; } = [];
    }

    [Test]
    public async Task ADictionaryOfLists_HasTwoLevelsOfRepetition()
    {
        var rows = new[]
        {
            new Maps { Lists = new() { ["a"] = [1, 2], ["b"] = [] } },
            new Maps(),
        };

        var bytes = Serialize(rows);

        var keys = Entries(bytes, "Lists.key_value.key");
        await Assert.That(keys.Definition).IsEquivalentTo(new[] { 1, 1, 0 });
        await Assert.That(keys.Repetition).IsEquivalentTo(new[] { 0, 1, 0 });
        await Assert.That(keys.Values).IsEquivalentTo(new object[] { "a", "b" });
        var values = Entries(bytes, "Lists.key_value.value.list.element");
        await Assert.That(values.Definition).IsEquivalentTo(new[] { 2, 2, 1, 0 }); // 1, 2 in "a"; the empty list of "b"; an empty map
        await Assert.That(values.Repetition).IsEquivalentTo(new[] { 0, 2, 1, 0 });
        await Assert.That(values.Values).IsEquivalentTo(new object[] { 1, 2 });
    }

    [Test]
    public async Task EveryKindOfDictionary_IsAMap()
    {
        var rows = new[]
        {
            new Maps
            {
                Simple = new Dictionary<string, int> { ["x"] = 1 },
                Texts = new Dictionary<int, string?> { [1] = "one", [2] = null },
                Sorted = new SortedDictionary<Guid, long?> { [new Guid(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] = 2, [new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] = null },
                Each = [new() { ["a"] = 1 }, [], new() { ["b"] = 2, ["c"] = 3 }],
            },
        };

        var bytes = Serialize(rows);

        await Assert.That(Entries(bytes, "Simple.key_value.value").Values).IsEquivalentTo(new object[] { 1 });
        var texts = Entries(bytes, "Texts.key_value.value");
        await Assert.That(texts.Definition).IsEquivalentTo(new[] { 2, 1 });
        await Assert.That(texts.Values).IsEquivalentTo(new object[] { "one" });
        var sorted = Entries(bytes, "Sorted.key_value.value");
        await Assert.That(sorted.Definition).IsEquivalentTo(new[] { 1, 2 }); // the key 1 first, with a null value, then the key 2
        var each = Entries(bytes, "Each.list.element.key_value.key");
        await Assert.That(each.Definition).IsEquivalentTo(new[] { 2, 1, 2, 2 });
        await Assert.That(each.Repetition).IsEquivalentTo(new[] { 0, 1, 1, 2 });
        await Assert.That(each.Values).IsEquivalentTo(new object[] { "a", "b", "c" });
        await Assert.That(ParquetSerializer.GetSchema<Maps>().Root.Children.Select(c => c.LogicalType)).Contains(LogicalType.Map);
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

        public List<Color> Palette { get; set; } = [];

        public Dictionary<Color, Permission?> Rights { get; set; } = [];
    }

    [Test]
    public async Task Enums_AreWrittenByName_AsEnumColumns()
    {
        var rows = new[]
        {
            new Paint { Color = Color.Blue, Accent = null, Access = Permission.Read | Permission.Write, Palette = [Color.Red, Color.Green], Rights = new() { [Color.Red] = Permission.None, [Color.Blue] = null } },
            new Paint { Color = Color.Red, Accent = Color.Green, Access = (Permission)4, Palette = [] },
        };

        var bytes = Serialize(rows);

        var schema = ParquetSerializer.GetSchema<Paint>();
        await Assert.That(schema.FindColumn(["Color"])!.LogicalType).IsEqualTo(LogicalType.Enum);
        await Assert.That(schema.FindColumn(["Color"])!.Repetition).IsEqualTo(FieldRepetitionType.Required);
        await Assert.That(schema.FindColumn(["Accent"])!.Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(Entries(bytes, "Color").Values).IsEquivalentTo(new object[] { "Blue", "Red" });
        var accent = Entries(bytes, "Accent");
        await Assert.That(accent.Definition).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(accent.Values).IsEquivalentTo(new object[] { "Green" });
        await Assert.That(Entries(bytes, "Access").Values).IsEquivalentTo(new object[] { "ReadWrite", "4" }); // a combination with no name is formatted as Enum.ToString does
        await Assert.That(Entries(bytes, "Palette.list.element").Values).IsEquivalentTo(new object[] { "Red", "Green" });
        await Assert.That(Entries(bytes, "Rights.key_value.key").Values).IsEquivalentTo(new object[] { "Red", "Blue" });
        await Assert.That(Entries(bytes, "Rights.key_value.value").Values).IsEquivalentTo(new object[] { "None" });
    }

    // ---- attributes reach the elements ----

    private sealed class Tuned
    {
        [ParquetDecimal(10, 2)]
        public List<decimal> Prices { get; set; } = [];

        [ParquetTimestamp(TimeUnit.Millis, IsAdjustedToUtc = false)]
        public List<DateTime?> Moments { get; set; } = [];

        [ParquetDecimal(5, 1)]
        public Dictionary<string, decimal> Weights { get; set; } = [];

        [ParquetEncoding(ParquetEncoding.DeltaBinaryPacked)]
        public List<int> Steady { get; set; } = [];

        [ParquetDecimal(7, 3)]
        public Dictionary<decimal, List<decimal>> Deep { get; set; } = [];

        [ParquetColumn("renamed")]
        public Inner Inside { get; set; } = new();
    }

    private sealed class Inner
    {
        [ParquetColumn("inner_name")]
        public string Name { get; set; } = "";

        [ParquetEncoding(ParquetEncoding.DeltaLengthByteArray)]
        public List<string> Words { get; set; } = [];
    }

    [Test]
    public async Task TheAttributesOfAProperty_ApplyToItsElements_KeysAndValues()
    {
        var schema = ParquetSerializer.GetSchema<Tuned>();

        await Assert.That(schema.FindColumn(["Prices", "list", "element"])!.LogicalType).IsEqualTo(LogicalType.Decimal(2, 10));
        await Assert.That(schema.FindColumn(["Prices", "list", "element"])!.Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(schema.FindColumn(["Moments", "list", "element"])!.LogicalType).IsEqualTo(LogicalType.Timestamp(false, TimeUnit.Millis));
        await Assert.That(schema.FindColumn(["Moments", "list", "element"])!.Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(schema.FindColumn(["Weights", "key_value", "value"])!.LogicalType).IsEqualTo(LogicalType.Decimal(1, 5));
        await Assert.That(schema.FindColumn(["Weights", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(schema.FindColumn(["renamed", "inner_name"])).IsNotNull();
        await Assert.That(schema.FindColumn(["Deep", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.Decimal(3, 7));
        await Assert.That(schema.FindColumn(["Deep", "key_value", "value", "list", "element"])!.LogicalType).IsEqualTo(LogicalType.Decimal(3, 7));
    }

    [Test]
    public async Task TheEncodingOfAProperty_ReachesItsColumns_ByPath()
    {
        var rows = Enumerable.Range(0, 300).Select(i => new Tuned { Steady = [i * 3, i * 3 + 1], Inside = new Inner { Name = $"n{i}", Words = [$"word {i}", "same"] } }).ToList();

        var file = TestParquetFile.Parse(Serialize(rows, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { DictionaryEnabled = false } }));

        int Index(string path) => file.Schema.FindColumn(path.Split('.'))!.ColumnIndex;
        await Assert.That(file.Metadata.RowGroups[0].Columns[Index("Steady.list.element")].MetaData!.Encodings).Contains(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(file.Metadata.RowGroups[0].Columns[Index("renamed.Words.list.element")].MetaData!.Encodings).Contains(ParquetEncoding.DeltaLengthByteArray);
        await Assert.That(file.Metadata.RowGroups[0].Columns[Index("renamed.inner_name")].MetaData!.Encodings).DoesNotContain(ParquetEncoding.DeltaLengthByteArray);
    }

    // ---- pages that would split a row ----

    [Test]
    public async Task SmallPages_NeverSplitARow_InNestedColumns()
    {
        var options = new ParquetSerializerOptions { RowGroupSize = 400, Writer = new ParquetWriterOptions { DataPageSize = 300, DictionaryEnabled = false } };
        var bytes = Serialize(Document.Rows(), options);

        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        using var reader = ReadHelper.Open(bytes);
        var expected = ReferenceShredder.Shred(reader.Schema, NestedDataset.Rows());
        await Assert.That(file.DataPages(0, 1).Count).IsGreaterThan(3);
        for (int column = 0; column < expected.Length; column++)
        {
            var (definition, repetition, values) = ReadHelper.ReadEntries(reader, 0, column, 37);

            await Assert.That(definition).IsEquivalentTo(expected[column].Definition).Because(NestedDataset.ColumnNames[column]);
            await Assert.That(repetition).IsEquivalentTo(expected[column].Repetition).Because(NestedDataset.ColumnNames[column]);
            await Assert.That(values.Select(NestedReaderTests.Describe).SequenceEqual(expected[column].Values.Select(NestedReaderTests.Describe))).IsTrue();
        }
    }

    // ---- nulls where there must be values ----

    private sealed class Holder
    {
        public Inner Inner { get; set; } = new();

        public List<string> Words { get; set; } = [];

        public List<Inner> Parts { get; set; } = [];

        public Dictionary<string, string> Pairs { get; set; } = [];
    }

    [Test]
    public async Task ANullWhereTheSchemaSaysThereIsAlwaysAValue_IsReported_WithTheRow()
    {
        (Action<Holder> Break, string Where)[] cases =
        [
            (h => h.Inner = null!, "Holder.Inner"),
            (h => h.Words = null!, "Holder.Words"),
            (h => h.Words = ["ok", null!], "Holder.Words"),
            (h => h.Parts = [new Inner(), null!], "Holder.Parts"),
            (h => h.Pairs = new() { ["k"] = null! }, "Holder.Pairs"),
        ];

        foreach (var (breakIt, where) in cases)
        {
            var rows = new[] { new Holder(), new Holder(), new Holder() };
            breakIt(rows[2]);

            var exception = Assert.Throws<ArgumentException>(() => Serialize(rows));

            await Assert.That(exception.Message).Contains(where);
            await Assert.That(exception.Message).Contains("row 2");
        }
    }

    // ---- models that cannot be written ----

    private sealed class Cyclic
    {
        public int Id { get; set; }

        public List<Cyclic> Children { get; set; } = [];
    }

    private sealed class SelfReference
    {
        public SelfReference? Next { get; set; }
    }

    private sealed class WithTimeSpan
    {
        public TimeSpan Duration { get; set; }
    }

    private sealed class WithObject
    {
        public object? Anything { get; set; }
    }

    private sealed class WithMatrix
    {
        public int[,] Cells { get; set; } = new int[0, 0];
    }

    private sealed class WithNullableKey
    {
#pragma warning disable CS8714 // the point of the test: a key type that allows null
        public Dictionary<int?, string> Map { get; set; } = [];
#pragma warning restore CS8714
    }

    private sealed class WithStructCollection
    {
        public ImmutableArray<int> Values { get; set; }
    }

    private sealed class WithEmptyObject
    {
        public Empty Nothing { get; set; } = new();
    }

    private sealed class Empty
    {
        [ParquetIgnore]
        public int Hidden { get; set; }
    }

    private sealed class DecimalOnObject
    {
        [ParquetDecimal(5, 2)]
        public Inner Inside { get; set; } = new();
    }

    private sealed class DecimalOnIntegers
    {
        [ParquetDecimal(5, 2)]
        public List<int> Values { get; set; } = [];
    }

    private sealed class DecimalOnText
    {
        [ParquetDecimal(5, 2)]
        public Dictionary<string, List<string>> Values { get; set; } = [];
    }

    private sealed class DecimalOnObjectMap
    {
        [ParquetDecimal(5, 2)]
        public Dictionary<string, Inner> Values { get; set; } = [];
    }

    private sealed class TimestampOnObjects
    {
        [ParquetTimestamp]
        public List<Inner> Values { get; set; } = [];
    }

    [Test]
    public async Task ModelsThatCannotBeWritten_AreReported()
    {
        await Assert.That(() => ParquetSerializer.GetSchema<Cyclic>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<SelfReference>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithTimeSpan>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithObject>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithMatrix>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithNullableKey>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithStructCollection>()).Throws<NotSupportedException>();
        await Assert.That(() => ParquetSerializer.GetSchema<WithEmptyObject>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<DecimalOnObject>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<DecimalOnIntegers>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<TimestampOnObjects>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<DecimalOnText>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<DecimalOnObjectMap>()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheMessageNamesTheProperty()
    {
        var cycle = Assert.Throws<NotSupportedException>(() => ParquetSerializer.GetSchema<Cyclic>());
        var duration = Assert.Throws<NotSupportedException>(() => ParquetSerializer.GetSchema<WithTimeSpan>());

        await Assert.That(cycle.Message).Contains("Cyclic.Children");
        await Assert.That(cycle.Message).Contains("contains itself");
        await Assert.That(duration.Message).Contains("WithTimeSpan.Duration");
        await Assert.That(duration.Message).Contains("TimeSpan");
    }

    // ---- the same object in two places is two groups ----

    private sealed class Twin
    {
        public Address? First { get; set; }

        public Address? Second { get; set; }

        public List<Address?> Others { get; set; } = [];
    }

    [Test]
    public async Task TheSameTypeInSeveralPlaces_IsSeveralGroups()
    {
        var address = new Address { City = "Rome", Zip = 100 };
        var bytes = Serialize([new Twin { First = address, Second = address, Others = [address, null, address] }, new Twin()]);

        var schema = ParquetSerializer.GetSchema<Twin>();
        await Assert.That(schema.Columns.Select(c => string.Join('.', c.Path))).IsEquivalentTo(new[]
        {
            "First.city", "First.zip", "Second.city", "Second.zip", "Others.list.element.city", "Others.list.element.zip",
        });
        await Assert.That(Entries(bytes, "First.city").Values).IsEquivalentTo(new object[] { "Rome" });
        await Assert.That(Entries(bytes, "Second.city").Values).IsEquivalentTo(new object[] { "Rome" });
        var others = Entries(bytes, "Others.list.element.city");
        await Assert.That(others.Definition).IsEquivalentTo(new[] { 3, 1, 3, 0 });
        await Assert.That(others.Repetition).IsEquivalentTo(new[] { 0, 1, 1, 0 });
    }

    // ---- attributes and encodings across dictionaries ----

    private sealed class MapAttributes
    {
        [ParquetDecimal(7, 3)]
        public Dictionary<decimal, string> DecimalKeys { get; set; } = [];

        [ParquetDecimal(7, 3)]
        public Dictionary<decimal, List<int>> DecimalKeysOfLists { get; set; } = [];

        [ParquetTime(TimeUnit.Millis)]
        public Dictionary<TimeOnly, string> TimeKeys { get; set; } = [];

        [ParquetTime(TimeUnit.Nanos)]
        public Dictionary<string, List<TimeOnly>> TimeValues { get; set; } = [];

        [ParquetTimestamp(TimeUnit.Millis)]
        public Dictionary<DateTime, string> TimestampKeys { get; set; } = [];

        [ParquetTimestamp(TimeUnit.Nanos)]
        public Dictionary<string, DateTimeOffset> TimestampValues { get; set; } = [];

        [ParquetEncoding(ParquetEncoding.DeltaLengthByteArray)]
        public Dictionary<string, string> Encoded { get; set; } = [];
    }

    private sealed class TimeOnText
    {
        [ParquetTime]
        public Dictionary<string, string> Values { get; set; } = [];
    }

    private sealed class TimestampOnNumbers
    {
        [ParquetTimestamp]
        public Dictionary<string, List<int>> Values { get; set; } = [];
    }

    [Test]
    public async Task TheAttributesOfADictionary_ApplyToWhicheverOfItsPartsTheySuit()
    {
        var schema = ParquetSerializer.GetSchema<MapAttributes>();

        await Assert.That(schema.FindColumn(["DecimalKeys", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.Decimal(3, 7));
        await Assert.That(schema.FindColumn(["DecimalKeys", "key_value", "value"])!.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(schema.FindColumn(["DecimalKeysOfLists", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.Decimal(3, 7));
        await Assert.That(schema.FindColumn(["DecimalKeysOfLists", "key_value", "value", "list", "element"])!.LogicalType).IsNull();
        await Assert.That(schema.FindColumn(["TimeKeys", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.Time(false, TimeUnit.Millis));
        await Assert.That(schema.FindColumn(["TimeKeys", "key_value", "key"])!.Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(schema.FindColumn(["TimeValues", "key_value", "value", "list", "element"])!.LogicalType).IsEqualTo(LogicalType.Time(false, TimeUnit.Nanos));
        await Assert.That(schema.FindColumn(["TimestampKeys", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.Timestamp(true, TimeUnit.Millis));
        await Assert.That(schema.FindColumn(["TimestampValues", "key_value", "value"])!.LogicalType).IsEqualTo(LogicalType.Timestamp(true, TimeUnit.Nanos));
        await Assert.That(schema.FindColumn(["TimestampValues", "key_value", "key"])!.LogicalType).IsEqualTo(LogicalType.String);
    }

    [Test]
    public async Task AnAttributeThatSuitsNothingInADictionary_IsReported()
    {
        await Assert.That(() => ParquetSerializer.GetSchema<TimeOnText>()).Throws<InvalidOperationException>();
        await Assert.That(() => ParquetSerializer.GetSchema<TimestampOnNumbers>()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheEncodingOfADictionary_ReachesItsKeysAndValues()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new MapAttributes { Encoded = new() { [$"key {i}"] = $"value {i}" } }).ToList();

        var file = TestParquetFile.Parse(Serialize(rows, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { DictionaryEnabled = false } }));

        foreach (string part in new[] { "key", "value" })
        {
            int column = file.Schema.FindColumn(["Encoded", "key_value", part])!.ColumnIndex;
            await Assert.That(file.Metadata.RowGroups[0].Columns[column].MetaData!.Encodings).Contains(ParquetEncoding.DeltaLengthByteArray).Because(part);
        }
    }

    // ---- structure that only counts when the parts are required ----

    private sealed class Outer
    {
        public Plain? Inner { get; set; }

        public List<Plain>? Items { get; set; }
    }

    private sealed class Plain
    {
        public int Number { get; set; }

        public string Text { get; set; } = "";
    }

    [Test]
    public async Task RequiredPropertiesInAnOptionalObject_HaveALevelForThePresenceOfTheObject()
    {
        var bytes = Serialize(new[] { new Outer(), new Outer { Inner = new Plain { Number = 1, Text = "a" }, Items = [new Plain { Number = 2, Text = "b" }] } });

        var number = Entries(bytes, "Inner.Number");
        await Assert.That(number.Definition).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(number.Values).IsEquivalentTo(new object[] { 1 });
        var items = Entries(bytes, "Items.list.element.Text");
        await Assert.That(items.Definition).IsEquivalentTo(new[] { 0, 2 }); // no list, then a list with an element
        await Assert.That(items.Repetition).IsEquivalentTo(new[] { 0, 0 });
    }

    // ---- types of the .NET libraries and keys that are null ----

    private sealed class WithStringBuilder
    {
        public System.Text.StringBuilder Text { get; set; } = new();
    }

    private sealed class NullKeys : IReadOnlyDictionary<string, int>
    {
        public IEnumerable<string> Keys => [null!];

        public IEnumerable<int> Values => [1];

        public int Count => 1;

        public int this[string key] => 1;

        public bool ContainsKey(string key) => true;

        public bool TryGetValue(string key, out int value)
        {
            value = 1;
            return true;
        }

        public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
        {
            yield return new KeyValuePair<string, int>(null!, 1);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WithNullKeys
    {
        public NullKeys Map { get; set; } = new();
    }

    [Test]
    public async Task ATypeOfTheLibrariesThatIsNotACollection_IsRefused_EvenInADeeperNamespace()
    {
        var exception = Assert.Throws<NotSupportedException>(() => ParquetSerializer.GetSchema<WithStringBuilder>());

        await Assert.That(exception.Message).Contains("StringBuilder");
    }

    [Test]
    public async Task ANullKey_IsReported()
    {
        var exception = Assert.Throws<ArgumentException>(() => Serialize(new[] { new WithNullKeys() }));

        await Assert.That(exception.Message).Contains("WithNullKeys.Map");
        await Assert.That(exception.Message).Contains("key");
        await Assert.That(exception.Message).Contains("row 0");
    }

    [Test]
    public async Task NullableKeys_AreRefused_WithAMessageAboutKeys()
    {
        var exception = Assert.Throws<NotSupportedException>(() => ParquetSerializer.GetSchema<WithNullableKey>());

        await Assert.That(exception.Message).Contains("keys");
    }
}
