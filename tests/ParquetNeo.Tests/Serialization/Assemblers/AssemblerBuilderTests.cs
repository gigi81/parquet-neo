using ParquetNeo.DataModel;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Tests.Serialization.Assemblers;

public class AssemblerBuilderTests
{
    private sealed class Flat
    {
        public int A { get; set; }

        public string? B { get; set; }
    }

    private sealed class Nested
    {
        public int A { get; set; }

        public Flat? Inner { get; set; }

        public List<Flat>? Items { get; set; }

        public Dictionary<string, Flat>? ByName { get; set; }

        public int[]? Numbers { get; set; }
    }

    [Test]
    public async Task ItCountsTheColumnsAndTheObjectsCollectionsAndDictionariesOfARowType()
    {
        var flat = new AssemblerBuilder();
        flat.BuildRow<Flat>();
        var nested = new AssemblerBuilder();
        nested.BuildRow<Nested>();

        await Assert.That(flat.SlotCount).IsEqualTo(2);
        await Assert.That(flat.NodeCount).IsEqualTo(1);

        // A, Inner.A, Inner.B, Items.A, Items.B, ByName key, ByName value.A, ByName value.B, Numbers.
        await Assert.That(nested.SlotCount).IsEqualTo(9);

        // The row, Inner, Items and its element, ByName and its value, Numbers.
        await Assert.That(nested.NodeCount).IsEqualTo(7);
    }

    private sealed class Cycle
    {
        public Cycle? Next { get; set; }
    }

    private sealed class Empty
    {
        public int ReadOnly => 1;
    }

    private sealed class Twice
    {
        public int A { get; set; }

        [ParquetColumn("A")]
        public int B { get; set; }
    }

    private sealed class Unmatched
    {
        public Unmatched(int first, string unknown)
        {
            First = first;
            Other = unknown;
        }

        public int First { get; }

        public string Other { get; }
    }

    private sealed class Mismatched
    {
        public Mismatched(string name) => Name = name.Length;

        public int Name { get; }
    }

    private sealed class WithUri
    {
        public Uri? Address { get; set; }
    }

    private sealed class NullableKeys
    {
#pragma warning disable CS8714 // The point is a key type that can be null.
        public Dictionary<int?, string>? Map { get; set; }
#pragma warning restore CS8714
    }

    private sealed class WithStructCollection
    {
        public ImmutableLike? Items { get; set; }
    }

    private struct ImmutableLike : ICollection<int>
    {
        public readonly int Count => 0;

        public readonly bool IsReadOnly => false;

        public readonly void Add(int item)
        {
        }

        public readonly void Clear()
        {
        }

        public readonly bool Contains(int item) => false;

        public readonly void CopyTo(int[] array, int arrayIndex)
        {
        }

        public readonly bool Remove(int item) => false;

        public readonly IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();

        readonly System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WithUncreatableInterface
    {
        public IQueryable<int>? Items { get; set; }
    }

    private sealed class WithCollectionWithoutConstructor
    {
        public Stack<int>? Items { get; set; }
    }

    /// <summary>An interface of a dictionary that a <see cref="Dictionary{TKey, TValue}"/> does not implement, so there is nothing to create for it.</summary>
    private interface IKeyedStore : IDictionary<string, int>
    {
    }

    private sealed class WithUncreatableDictionary
    {
        public IKeyedStore? Items { get; set; }
    }

    [Test]
    public async Task ATypeThatContainsItself_IsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<Cycle>());

        await Assert.That(exception.Message).Contains("contains itself");
    }

    [Test]
    public async Task ATypeWithNothingToRead_IsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AssemblerBuilder().BuildRow<Empty>());

        await Assert.That(exception.Message).Contains("has no public properties with a setter to read into");
    }

    [Test]
    public async Task TwoPropertiesForOneColumn_AreRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AssemblerBuilder().BuildRow<Twice>());

        await Assert.That(exception.Message).Contains("Two properties of Twice are read from a column named 'A'");
    }

    [Test]
    public async Task AConstructorWhoseParametersAreNotAllProperties_IsNotUsed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AssemblerBuilder().BuildRow<Unmatched>());

        await Assert.That(exception.Message).Contains("has no public constructor without parameters and none whose parameters are all properties");
    }

    [Test]
    public async Task AConstructorParameterOfAnotherTypeThanItsProperty_IsNotUsed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AssemblerBuilder().BuildRow<Mismatched>());

        await Assert.That(exception.Message).Contains("has no public constructor without parameters");
    }

    [Test]
    public async Task ASystemTypeThatIsNotAColumn_IsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<WithUri>());

        await Assert.That(exception.Message).Contains("cannot be read as a column");
    }

    [Test]
    public async Task ADictionaryWithNullableKeys_IsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<NullableKeys>());

        await Assert.That(exception.Message).Contains("the keys of a map cannot be null");
    }

    [Test]
    public async Task ACollectionThatIsAStruct_IsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<WithStructCollection>());

        await Assert.That(exception.Message).Contains("a collection that is a struct, which is not supported");
    }

    [Test]
    public async Task AnInterfaceThatCannotBeCreated_IsRefusedWithWhatToUseInstead()
    {
        var list = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<WithUncreatableInterface>());
        var dictionary = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<WithUncreatableDictionary>());

        await Assert.That(list.Message).Contains("IQueryable`1, an interface that cannot be created");
        await Assert.That(dictionary.Message).Contains("an interface that cannot be created");
    }

    [Test]
    public async Task ACollectionWithoutAPublicConstructor_IsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new AssemblerBuilder().BuildRow<WithCollectionWithoutConstructor>());

        await Assert.That(exception.Message).IsNotEmpty();
    }
}
