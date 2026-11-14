using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Describes how a row type is written as columns, and gets the code that gives the values of each object to them. What the source
/// generator writes for a type uses it, and nothing at run time looks at the type: no reflection, and no code is compiled. Not for use in
/// code you write yourself.
/// </summary>
/// <remarks>
/// The properties are described in the order of their columns, depth first: <see cref="Leaf{TValue}"/> for one that is a column of its
/// own, and for an object, a collection or a dictionary a Begin call, what is inside it, and <see cref="End"/>. In a collection the
/// element is described, and in a dictionary the key and then the value; they have no names.
/// </remarks>
public sealed class WriteMappingBuilder<TRow>
{
    private readonly List<LeafInfo> _leaves = [];
    private readonly Stack<WriteScope> _scopes = new();

    /// <param name="shred">Gives the properties of a row to what was described for them.</param>
    public WriteMappingBuilder(ShredRow<TRow> shred)
    {
        ArgumentNullException.ThrowIfNull(shred);
        _scopes.Push(new ObjectWriteScope<TRow>(name: null, path: "", $"Row type '{typeof(TRow).Name}'", optional: false, new Position(0, 0), firstColumn: 0, shred));
    }

    /// <summary>Describes a property, or an element, a key or a value, that is a column of its own.</summary>
    /// <param name="name">The name of the column in the file; null for an element, a key or a value.</param>
    /// <param name="description">What it is, for messages: "Property 'Order.Id'"; null for an element, a key or a value.</param>
    public WriteMappingBuilder<TRow> Leaf<TValue>(string? name, string? description, bool optional, ColumnSettings? settings = null, ParquetEncoding? encoding = null, DictionarySettings? dictionary = null)
    {
        var scope = _scopes.Peek();
        string path = scope.ChildPath(name);
        string described = scope.ChildDescription(description);
        var position = scope.ChildPosition;
        var type = ColumnTypeRegistry.Get<TValue>().Configure(settings ?? new ColumnSettings());
        bool definitionLevels = (optional ? position.Definition + 1 : position.Definition) > 0;
        bool repetitionLevels = position.Repetition > 0;
        int column = _leaves.Count;
        _leaves.Add(new LeafInfo(path, encoding, dictionary, capacity => new LeafBuffer<TValue>(definitionLevels, repetitionLevels, type, capacity)));
        scope.Accept(name, new LeafShredder<TValue>(column, optional, type, described));
        return this;
    }

    /// <summary>Begins describing a property, element, key or value that is an object, until <see cref="End"/>.</summary>
    /// <param name="shred">Gives the properties of the object to what is described inside it.</param>
    public WriteMappingBuilder<TRow> BeginObject<TValue>(string? name, string? description, bool optional, ShredRow<TValue> shred)
    {
        ArgumentNullException.ThrowIfNull(shred);
        var parent = _scopes.Peek();
        _scopes.Push(new ObjectWriteScope<TValue>(name, parent.ChildPath(name), parent.ChildDescription(description), optional, parent.ChildPosition, _leaves.Count, shred));
        return this;
    }

    /// <summary>Begins describing a collection whose elements cannot be null, until <see cref="End"/>, with the element described inside.</summary>
    public WriteMappingBuilder<TRow> BeginList<TCollection, TElement>(string? name, string? description, bool optional)
        where TCollection : class, IEnumerable<TElement>
    {
        var parent = _scopes.Peek();
        _scopes.Push(new ListWriteScope<TCollection, TElement, TElement>(name, parent.ChildPath(name), parent.ChildDescription(description), optional, parent.ChildPosition, _leaves.Count, Getters.DirectItem<TElement>()));
        return this;
    }

    /// <summary>Begins describing a collection of <see cref="Nullable{T}"/>s, until <see cref="End"/>, with the element described inside.</summary>
    public WriteMappingBuilder<TRow> BeginNullableList<TCollection, TElement>(string? name, string? description, bool optional)
        where TCollection : class, IEnumerable<TElement?>
        where TElement : struct
    {
        var parent = _scopes.Peek();
        _scopes.Push(new ListWriteScope<TCollection, TElement?, TElement>(name, parent.ChildPath(name), parent.ChildDescription(description), optional, parent.ChildPosition, _leaves.Count, Getters.NullableItem<TElement>()));
        return this;
    }

    /// <summary>Begins describing a dictionary, until <see cref="End"/>, with the key and then the value described inside.</summary>
    public WriteMappingBuilder<TRow> BeginMap<TMap, TKey, TValue>(string? name, string? description, bool optional)
        where TMap : class, IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : notnull
    {
        var parent = _scopes.Peek();
        _scopes.Push(new MapWriteScope<TMap, TKey, TValue, TValue>(name, parent.ChildPath(name), parent.ChildDescription(description), optional, parent.ChildPosition, _leaves.Count, Getters.DirectItem<TValue>()));
        return this;
    }

    /// <summary>Begins describing a dictionary whose values are <see cref="Nullable{T}"/>s, until <see cref="End"/>, with the key and then the value described inside.</summary>
    public WriteMappingBuilder<TRow> BeginMapOfNullable<TMap, TKey, TValue>(string? name, string? description, bool optional)
        where TMap : class, IEnumerable<KeyValuePair<TKey, TValue?>>
        where TKey : notnull
        where TValue : struct
    {
        var parent = _scopes.Peek();
        _scopes.Push(new MapWriteScope<TMap, TKey, TValue?, TValue>(name, parent.ChildPath(name), parent.ChildDescription(description), optional, parent.ChildPosition, _leaves.Count, Getters.NullableItem<TValue>()));
        return this;
    }

    /// <summary>Ends the object, collection or dictionary that was begun last.</summary>
    public WriteMappingBuilder<TRow> End()
    {
        if (_scopes.Count < 2)
        {
            throw new InvalidOperationException("End was called with nothing begun.");
        }

        var scope = _scopes.Pop();
        _scopes.Peek().Accept(scope.Name, scope.Finish(_leaves.Count - scope.FirstColumn));
        return this;
    }

    /// <exception cref="InvalidOperationException">Something was begun and not ended, or no column was described.</exception>
    public WriteMapping<TRow> Build()
    {
        if (_scopes.Count != 1)
        {
            throw new InvalidOperationException("Something was begun and not ended.");
        }

        if (_leaves.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(TRow).Name} has no columns to write.");
        }

        var root = (StructShredder<TRow>)_scopes.Peek().Finish(_leaves.Count);
        return new WriteMapping<TRow>(new RowModel<TRow>(root, _leaves.ToArray()));
    }
}
