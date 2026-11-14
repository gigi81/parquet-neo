using System.Collections.Immutable;
using ParquetNeo.DataModel;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;

namespace ParquetNeo.Serialization.Tests;

public class NestedDeserializerShapeTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    private static List<TRow> Read<TRow>(byte[] bytes) => ParquetSerializer.Deserialize<TRow>(new MemoryStream(bytes));

    private static List<TRow> RoundTrip<TRow>(IEnumerable<TRow> rows) => Read<TRow>(Serialize(rows));

    [Test]
    public async Task TheDeepComparer_SeesDifferences()
    {
        var a = Document.Row(7);
        var b = Document.Row(7);

        await Assert.That(DeepComparer.Difference(a, b)).IsNull();
        b.Labels.Add("extra");
        await Assert.That(DeepComparer.Difference(a, b)).Contains("Labels");
        b = Document.Row(7);
        b.Attributes = new() { ["x"] = 1 };
        await Assert.That(DeepComparer.Difference(a, b)).IsNotNull();
        b = Document.Row(2);
        b.Id = 2;
        await Assert.That(DeepComparer.Difference(Document.Row(2), b)).IsNull();
        b.Person = new Person { Name = "different" };
        await Assert.That(DeepComparer.Difference(Document.Row(2), b)).Contains("Person");
    }

    // ---- kinds of collections ----

    private sealed class Collections
    {
        public int[] Array { get; set; } = [];

        public IEnumerable<int> Lazy { get; set; } = [];

        public IReadOnlyList<string?> ReadOnly { get; set; } = [];

        public HashSet<long> Set { get; set; } = [];

        public IList<double> Interface { get; set; } = [];

        public ICollection<bool> Bits { get; set; } = [];

        public ISet<int> Unique { get; set; } = new HashSet<int>();

        public LinkedList<string> Linked { get; set; } = new();

        public int[]? Optional { get; set; }
    }

    [Test]
    public async Task EveryKindOfCollection_IsCreatedAsTheProperty_Asks()
    {
        var rows = new[]
        {
            new Collections
            {
                Array = [1, 2], Lazy = [3, 4, 5], ReadOnly = new List<string?> { "a", null }, Set = [5L], Interface = new List<double> { 1.5 }, Bits = new[] { true, false },
                Unique = new HashSet<int> { 9, 8 }, Linked = new LinkedList<string>(["x", "y"]), Optional = [9],
            },
            new Collections { Optional = null },
            new Collections { Array = [3], ReadOnly = ["z"], Set = [6L, 7L], Interface = new[] { 2.5, 3.5 }, Bits = new List<bool> { true }, Optional = [] },
        };

        var read = RoundTrip(rows);

        await Assert.That(read.Count).IsEqualTo(3);
        await Assert.That(read[0].Array).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(read[0].Lazy.ToArray()).IsEquivalentTo(new[] { 3, 4, 5 });
        await Assert.That(read[0].ReadOnly).IsEquivalentTo(new string?[] { "a", null });
        await Assert.That(read[0].Set.SequenceEqual([5L])).IsTrue();
        await Assert.That(read[0].Interface).IsEquivalentTo(new[] { 1.5 });
        await Assert.That(read[0].Bits).IsEquivalentTo(new[] { true, false });
        await Assert.That(read[0].Unique.OrderBy(x => x)).IsEquivalentTo(new[] { 8, 9 });
        await Assert.That(read[0].Linked.ToArray()).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(read[0].Optional).IsEquivalentTo(new[] { 9 });
        await Assert.That(read[1].Optional).IsNull();
        await Assert.That(read[1].Array).IsEmpty();
        await Assert.That(read[2].Optional).IsEmpty();
        await Assert.That(read[2].Set.OrderBy(x => x)).IsEquivalentTo(new[] { 6L, 7L });
        await Assert.That(read[0].Array.GetType()).IsEqualTo(typeof(int[]));
        await Assert.That(read[0].Lazy.GetType()).IsEqualTo(typeof(List<int>));
        await Assert.That(read[0].Unique.GetType()).IsEqualTo(typeof(HashSet<int>));
    }

    private sealed class WithQueue
    {
        public Queue<int> Queue { get; set; } = new();
    }

    private sealed class WithImmutable
    {
        public ImmutableList<int> Values { get; set; } = [];
    }


    [Test]
    public async Task ACollectionThatCannotBeFilled_IsReported_WithTheProperty()
    {
        var queue = Assert.Throws<NotSupportedException>(() => Read<WithQueue>(Serialize(new[] { new WithQueue() })));
        var immutable = Assert.Throws<NotSupportedException>(() => Read<WithImmutable>(Serialize(new[] { new WithImmutable() })));

        await Assert.That(queue.Message).Contains("WithQueue.Queue");
        await Assert.That(queue.Message).Contains("ICollection");
        await Assert.That(immutable.Message).Contains("WithImmutable.Values");
    }

    // ---- kinds of dictionaries ----

    private sealed class Maps
    {
        public Dictionary<string, List<int>> Lists { get; set; } = [];

        public IDictionary<string, int> Simple { get; set; } = new Dictionary<string, int>();

        public IReadOnlyDictionary<int, string?> Texts { get; set; } = new Dictionary<int, string?>();

        public SortedDictionary<Guid, long?> Sorted { get; set; } = [];

        public List<Dictionary<string, int>> Each { get; set; } = [];

        public SortedList<string, DateTime> Moments { get; set; } = [];
    }

    [Test]
    public async Task EveryKindOfDictionary_IsCreatedAsThePropertyAsks()
    {
        var rows = new[]
        {
            new Maps
            {
                Lists = new() { ["a"] = [1, 2], ["b"] = [] },
                Simple = new Dictionary<string, int> { ["x"] = 1 },
                Texts = new Dictionary<int, string?> { [1] = "one", [2] = null },
                Sorted = new SortedDictionary<Guid, long?> { [new Guid(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] = 2, [new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] = null },
                Each = [new() { ["a"] = 1 }, [], new() { ["b"] = 2, ["c"] = 3 }],
                Moments = new SortedList<string, DateTime> { ["first"] = DateTime.UnixEpoch, ["second"] = DateTime.UnixEpoch.AddDays(1) },
            },
            new Maps(),
        };

        var read = RoundTrip(rows);

        await Assert.That(DeepComparer.Difference(rows[0].Lists, read[0].Lists)).IsNull();
        await Assert.That(DeepComparer.Difference(rows[0].Simple, read[0].Simple)).IsNull();
        await Assert.That(read[0].Texts[1]).IsEqualTo("one");
        await Assert.That(read[0].Texts[2]).IsNull();
        await Assert.That(read[0].Texts.Count).IsEqualTo(2);
        await Assert.That(read[0].Sorted.Values).IsEquivalentTo(new long?[] { null, 2 });
        await Assert.That(DeepComparer.Difference(rows[0].Each, read[0].Each)).IsNull();
        await Assert.That(read[0].Moments.Keys).IsEquivalentTo(new[] { "first", "second" });
        await Assert.That(read[1].Lists).IsEmpty();
        await Assert.That(read[1].Each).IsEmpty();
        await Assert.That(read[0].Simple.GetType()).IsEqualTo(typeof(Dictionary<string, int>));
        await Assert.That(read[0].Sorted.GetType()).IsEqualTo(typeof(SortedDictionary<Guid, long?>));
    }

    // ---- objects ----

    private sealed record Address(string City, int? Zip);

    private sealed record Customer(string Name, Address Home, Address? Work, List<Address> Others);

    private struct Coordinates
    {
        public double Latitude { get; set; }

        public double Longitude { get; set; }
    }

    private readonly struct Tag
    {
        public Tag(string name, int weight)
        {
            Name = name;
            Weight = weight;
        }

        public string Name { get; }

        public int Weight { get; }
    }

    private sealed class Place
    {
        public string Name { get; set; } = "";

        public Coordinates Where { get; set; }

        public Coordinates? Elsewhere { get; set; }

        public List<Tag> Tags { get; set; } = [];

        public Dictionary<string, Coordinates> Landmarks { get; set; } = [];
    }

    [Test]
    public async Task NestedRecords_AreBuiltByTheirConstructors()
    {
        var rows = new[]
        {
            new Customer("Ann", new Address("Rome", 100), null, []),
            new Customer("Bob", new Address("Oslo", null), new Address("Paris", 75000), [new Address("A", 1), new Address("B", null)]),
        };

        var read = RoundTrip(rows);

        await Assert.That(DeepComparer.Difference(rows, read)).IsNull();
        await Assert.That(read[0].Work).IsNull();
        await Assert.That(read[1].Others[1].Zip).IsNull();
    }

    [Test]
    public async Task NestedStructs_AreBuiltInPlace_OrByAConstructor()
    {
        var rows = new[]
        {
            new Place { Name = "a", Where = new Coordinates { Latitude = 1, Longitude = 2 }, Elsewhere = new Coordinates { Latitude = 3, Longitude = 4 }, Tags = [new Tag("x", 1), new Tag("y", 2)], Landmarks = new() { ["tower"] = new Coordinates { Latitude = 5, Longitude = 6 } } },
            new Place { Name = "b", Elsewhere = null },
        };

        var read = RoundTrip(rows);

        await Assert.That(read[0].Where.Latitude).IsEqualTo(1.0);
        await Assert.That(read[0].Where.Longitude).IsEqualTo(2.0);
        await Assert.That(read[0].Elsewhere!.Value.Longitude).IsEqualTo(4.0);
        await Assert.That(read[0].Tags.Select(t => (t.Name, t.Weight))).IsEquivalentTo(new[] { ("x", 1), ("y", 2) });
        await Assert.That(read[0].Landmarks["tower"].Latitude).IsEqualTo(5.0);
        await Assert.That(read[1].Elsewhere).IsNull();
        await Assert.That(read[1].Tags).IsEmpty();
    }

    // ---- enums in collections ----

    private enum Color
    {
        Red,
        Green,
        Blue,
    }

    private sealed class Paint
    {
        public List<Color> Palette { get; set; } = [];

        public Dictionary<Color, Color?> Matches { get; set; } = [];
    }

    [Test]
    public async Task EnumsInCollectionsAndDictionaries_AreRead()
    {
        var rows = new[] { new Paint { Palette = [Color.Blue, Color.Red], Matches = new() { [Color.Red] = Color.Green, [Color.Blue] = null } } };

        var read = RoundTrip(rows);

        await Assert.That(read[0].Palette).IsEquivalentTo(new[] { Color.Blue, Color.Red });
        await Assert.That(read[0].Matches[Color.Red]).IsEqualTo(Color.Green);
        await Assert.That(read[0].Matches[Color.Blue]).IsNull();
    }

    // ---- only some of what the file has ----

    private sealed class Slim
    {
        [ParquetColumn("id")]
        public long Id { get; set; }

        [ParquetColumn("person")]
        public SlimPerson? Person { get; set; }

        [ParquetColumn("events")]
        public List<SlimEvent?>? Events { get; set; }
    }

    private sealed class SlimPerson
    {
        [ParquetColumn("address")]
        public SlimAddress? Address { get; set; }

        [ParquetColumn("missing")]
        public string? NotThere { get; set; }
    }

    private sealed class SlimAddress
    {
        [ParquetColumn("zip")]
        public int? Zip { get; set; }
    }

    private sealed class SlimEvent
    {
        [ParquetColumn("count")]
        public int? Count { get; set; }

        [ParquetColumn("not_there")]
        public int[]? Nothing { get; set; }
    }

    [Test]
    public async Task ARowTypeCanReadJustSomeOfTheNestedColumns_AndColumnsTheFileLacksAreNull()
    {
        var documents = Document.Rows();
        var bytes = Serialize(documents);

        var read = Read<Slim>(bytes);
        var asynchronous = await ParquetSerializer.DeserializeAsync<Slim>(new MemoryStream(bytes));

        foreach (var rows in new[] { read, asynchronous })
        {
            await Assert.That(rows.Count).IsEqualTo(200);
            for (int i = 0; i < rows.Count; i++)
            {
                await Assert.That(rows[i].Id).IsEqualTo(documents[i].Id);
                await Assert.That(rows[i].Person is null).IsEqualTo(documents[i].Person is null).Because($"person {i}");
                await Assert.That(rows[i].Person?.Address is null).IsEqualTo(documents[i].Person?.Address is null).Because($"address {i}");
                await Assert.That(rows[i].Person?.Address?.Zip).IsEqualTo(documents[i].Person?.Address?.Zip).Because($"zip {i}");
                await Assert.That(rows[i].Person?.NotThere).IsNull();
                await Assert.That(rows[i].Events?.Count).IsEqualTo(documents[i].Events?.Count).Because($"events {i}");
                await Assert.That(string.Join(',', rows[i].Events?.Select(e => e?.Count.ToString() ?? "-") ?? ["none"]))
                    .IsEqualTo(string.Join(',', documents[i].Events?.Select(e => e?.Count.ToString() ?? "-") ?? ["none"])).Because($"counts {i}");
                await Assert.That(rows[i].Events?.All(e => e is null || e.Nothing is null) ?? true).IsTrue();
            }
        }
    }

    // ---- what the file does not have for something that cannot be null ----

    private sealed class NeedsMore
    {
        [ParquetColumn("person")]
        public NeedsMorePerson? Person { get; set; }
    }

    private sealed class NeedsMorePerson
    {
        [ParquetColumn("name")]
        public string? Name { get; set; }

        [ParquetColumn("nickname")]
        public string Nickname { get; set; } = "";
    }

    private sealed class NeedsAnObject
    {
        [ParquetColumn("not_there")]
        public Address Required { get; set; } = new("", null);
    }

    [Test]
    public async Task ARequiredFieldTheFileLacks_NamesTheGroupAndTheField()
    {
        var bytes = Serialize(Document.Rows(5));

        var field = Assert.Throws<SchemaMismatchException>(() => Read<NeedsMore>(bytes));
        var column = Assert.Throws<SchemaMismatchException>(() => Read<NeedsAnObject>(bytes));

        await Assert.That(field.Message).Contains("'person'");
        await Assert.That(field.Message).Contains("'nickname'");
        await Assert.That(field.Message).Contains("NeedsMorePerson.Nickname");
        await Assert.That(field.Message).Contains("'name'");
        await Assert.That(field.Message).Contains("has no field named 'nickname'");
        await Assert.That(column.Message).Contains("not_there");
        await Assert.That(column.Message).Contains("has no column named 'not_there'");
    }

    // ---- shapes that do not fit ----

    private sealed class ListFromColumn
    {
        [ParquetColumn("id")]
        public List<long> Id { get; set; } = [];
    }

    private sealed class ScalarFromList
    {
        [ParquetColumn("scores")]
        public int? Scores { get; set; }
    }

    private sealed class ObjectFromList
    {
        [ParquetColumn("scores")]
        public Address? Scores { get; set; }
    }

    private sealed class ObjectFromColumn
    {
        [ParquetColumn("id")]
        public Address? Id { get; set; }
    }

    private sealed class DictionaryFromList
    {
        [ParquetColumn("scores")]
        public Dictionary<string, int>? Scores { get; set; }
    }

    private sealed class ListFromMap
    {
        [ParquetColumn("attributes")]
        public List<int>? Attributes { get; set; }
    }

    private sealed class DeeperList
    {
        [ParquetColumn("scores")]
        public List<List<int?>>? Scores { get; set; }
    }

    private sealed class ShallowerList
    {
        [ParquetColumn("matrix")]
        public List<int?>? Matrix { get; set; }
    }

    private sealed class WrongElement
    {
        [ParquetColumn("scores")]
        public List<string?>? Scores { get; set; }
    }

    private sealed class WrongKey
    {
        [ParquetColumn("attributes")]
        public Dictionary<int, int?>? Attributes { get; set; }
    }

    private sealed class ObjectFromMap
    {
        [ParquetColumn("attributes")]
        public Address? Attributes { get; set; }
    }

    [Test]
    public async Task AShapeTheFileDoesNotHave_IsReported()
    {
        var bytes = Serialize(Document.Rows(5));
        (Action Read, string Message)[] cases =
        [
            (() => Read<ListFromColumn>(bytes), "not a list"),
            (() => Read<ScalarFromList>(bytes), "group"),
            (() => Read<ObjectFromList>(bytes), "LIST"),
            (() => Read<ObjectFromColumn>(bytes), "single column"),
            (() => Read<DictionaryFromList>(bytes), "not a map"),
            (() => Read<ListFromMap>(bytes), "not a list"),
            (() => Read<DeeperList>(bytes), "not a list"),
            (() => Read<ShallowerList>(bytes), "group"),
            (() => Read<WrongElement>(bytes), "INT32"),
            (() => Read<WrongKey>(bytes), "BYTE_ARRAY"),
            (() => Read<ObjectFromMap>(bytes), "MAP"),
        ];

        foreach (var (read, message) in cases)
        {
            var exception = Assert.Throws<SchemaMismatchException>(() => read());

            await Assert.That(exception.Message).Contains(message);
        }
    }

    // ---- nulls where there must be none ----

    private sealed class Strict
    {
        [ParquetColumn("scores")]
        public List<int> Scores { get; set; } = [];
    }

    private sealed class StrictObjects
    {
        [ParquetColumn("events")]
        public List<SlimEventRequired> Events { get; set; } = [];
    }

    private sealed class SlimEventRequired
    {
        [ParquetColumn("count")]
        public int? Count { get; set; }
    }

    private sealed class StrictMap
    {
        [ParquetColumn("attributes")]
        public Dictionary<string, int> Attributes { get; set; } = [];
    }

    private sealed class StrictPerson
    {
        [ParquetColumn("person")]
        public SlimPersonRequired Person { get; set; } = new();
    }

    private sealed class SlimPersonRequired
    {
        [ParquetColumn("name")]
        public string? Name { get; set; }
    }

    [Test]
    public async Task ANullWhereTheRowTypeAllowsNone_IsReported_WithTheRow()
    {
        var bytes = Serialize(Document.Rows(30));

        var element = Assert.Throws<ValueConversionException>(() => Read<Strict>(bytes)); // row 0 has no list at all: a null list
        var item = Assert.Throws<ValueConversionException>(() => Read<StrictObjects>(bytes));
        var value = Assert.Throws<ValueConversionException>(() => Read<StrictMap>(bytes));
        var person = Assert.Throws<ValueConversionException>(() => Read<StrictPerson>(bytes));

        await Assert.That(element.Message).Contains("Strict.Scores");
        await Assert.That(element.Message).Contains("row 0");
        await Assert.That(item.Message).Contains("StrictObjects.Events");
        await Assert.That(value.Message).Contains("StrictMap.Attributes");
        await Assert.That(person.Message).Contains("StrictPerson.Person");
        await Assert.That(person.Message).Contains("row 0");
    }

    // ---- types that cannot be read ----

    private sealed class Cyclic
    {
        public int Id { get; set; }

        public List<Cyclic> Children { get; set; } = [];
    }

    private sealed class WithImmutableArray
    {
        public ImmutableArray<int> Values { get; set; }
    }

    private sealed class WithNullableKeys
    {
#pragma warning disable CS8714 // the point of the test: a key type that allows null
        public Dictionary<int?, string> Map { get; set; } = [];
#pragma warning restore CS8714
    }

    [Test]
    public async Task TypesThatCannotBeRead_AreReported_WithTheProperty()
    {
        var bytes = Serialize(Document.Rows(2));

        var cycle = Assert.Throws<NotSupportedException>(() => Read<Cyclic>(bytes));
        var immutable = Assert.Throws<NotSupportedException>(() => Read<WithImmutableArray>(bytes));
        var keys = Assert.Throws<NotSupportedException>(() => Read<WithNullableKeys>(bytes));

        await Assert.That(cycle.Message).Contains("Cyclic.Children");
        await Assert.That(cycle.Message).Contains("contains itself");
        await Assert.That(immutable.Message).Contains("WithImmutableArray.Values");
        await Assert.That(keys.Message).Contains("keys");
    }

    // ---- layouts that a row type cannot be matched to ----

    private sealed class KeysOnly
    {
        [ParquetColumn("m")]
        public Dictionary<string, int>? Map { get; set; }
    }

    private sealed class Inside
    {
        [ParquetColumn("a")]
        public int A { get; set; }
    }

    private sealed class ObjectFromRepeatedGroup
    {
        [ParquetColumn("g")]
        public Inside? Group { get; set; }
    }

    [Test]
    public async Task AMapWithoutValues_AndARepeatedGroupThatIsNotAList_AreReported()
    {
        var keysOnly = new MemoryStream();
        var map = new GroupNode("m", FieldRepetitionType.Optional,
            [new GroupNode("key_value", FieldRepetitionType.Repeated, [new PrimitiveNode("key", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String)])], LogicalType.Map);
        using (var writer = new ParquetFileWriter(keysOnly, new ParquetSchema([map]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteStrings(["a"], [2], [0]);
        }

        var repeated = new MemoryStream();
        var bare = new GroupNode("g", FieldRepetitionType.Repeated, [new PrimitiveNode("a", PhysicalType.Int32, FieldRepetitionType.Required)]);
        using (var writer = new ParquetFileWriter(repeated, new ParquetSchema([bare]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2], [1, 1], [0, 1]);
        }

        var noValues = Assert.Throws<SchemaMismatchException>(() => Read<KeysOnly>(keysOnly.ToArray()));
        var notAList = Assert.Throws<SchemaMismatchException>(() => Read<ObjectFromRepeatedGroup>(repeated.ToArray()));

        await Assert.That(noValues.Message).Contains("no values");
        await Assert.That(notAList.Message).Contains("is repeated, but the property is a single object");
    }

    // ---- the work done by the async paths ----

    [Test]
    public async Task TheAsyncPaths_ReadOnlyTheNestedColumnsTheRowTypeNeeds()
    {
        var bytes = Serialize(Document.Rows(3000));
        var counting = new CountingStream(bytes);

        var rows = await ParquetSerializer.DeserializeAsync<Slim>(counting);

        await Assert.That(rows.Count).IsEqualTo(3000);
        await Assert.That(counting.SyncReads).IsEqualTo(0);
        await Assert.That(counting.BytesRead).IsLessThan(bytes.Length / 2);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _asynchronous;

        public long BytesRead { get; private set; }

        public int SyncReads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_asynchronous == 0)
            {
                SyncReads++;
            }

            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _asynchronous++;
            try
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            finally
            {
                _asynchronous--;
            }
        }
    }
}
