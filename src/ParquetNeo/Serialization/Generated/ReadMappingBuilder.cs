using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Describes how a row type is read from columns, and gets the code that creates each object from their values. What the source
/// generator writes for a type uses it, and nothing at run time looks at the type: no reflection, and no code is compiled. Not for use in
/// code you write yourself.
/// </summary>
/// <remarks>
/// The properties are described in the same way as for a <see cref="WriteMappingBuilder{TRow}"/>: in the order of their columns,
/// depth first, with a Begin call and <see cref="End"/> around what is inside an object, a collection or a dictionary.
/// </remarks>
public sealed class ReadMappingBuilder<TRow>
{
    private readonly Stack<ReadScope> _scopes = new();
    private int _columns;
    private int _nodes;
    private int _contexts;

    /// <param name="assemble">Creates a row from what the properties read.</param>
    public ReadMappingBuilder(AssembleRow<TRow> assemble)
    {
        ArgumentNullException.ThrowIfNull(assemble);
        _scopes.Push(new ObjectReadScope<TRow>(name: null, $"Row type '{typeof(TRow).Name}'", optional: false, repetition: 0, _nodes++, firstColumn: 0, assemble));
    }

    /// <summary>Describes a property, or an element, a key or a value, that is a column of its own.</summary>
    /// <param name="name">The name of the column in the file; null for an element, a key or a value.</param>
    /// <param name="description">What it is, for messages: "Property 'Order.Id'"; null for an element, a key or a value.</param>
    public ReadMappingBuilder<TRow> Leaf<TValue>(string? name, string? description, bool optional)
    {
        var scope = _scopes.Peek();
        string described = scope.ChildDescription(description);
        scope.Accept(name, described, optional, new LeafAssembler<TValue>(_columns++, scope.ChildRepetition, described));
        return this;
    }

    /// <summary>Begins describing a property, element, key or value that is an object, until <see cref="End"/>.</summary>
    /// <param name="assemble">Creates the object from what its properties read.</param>
    public ReadMappingBuilder<TRow> BeginObject<TValue>(string? name, string? description, bool optional, AssembleRow<TValue> assemble)
    {
        ArgumentNullException.ThrowIfNull(assemble);
        var parent = _scopes.Peek();
        _scopes.Push(new ObjectReadScope<TValue>(name, parent.ChildDescription(description), optional, parent.ChildRepetition, _nodes++, _columns, assemble));
        return this;
    }

    /// <summary>Begins describing a collection whose elements cannot be null, until <see cref="End"/>, with the element described inside.</summary>
    /// <param name="elementOptional">Whether an element can be null.</param>
    /// <param name="create">Creates the collection to add the elements to; null for an array, a <see cref="List{T}"/> or an interface that one implements, which are made from what was read.</param>
    public ReadMappingBuilder<TRow> BeginList<TCollection, TElement>(string? name, string? description, bool optional, bool elementOptional, Func<ICollection<TElement>>? create = null)
        where TCollection : class
    {
        var parent = _scopes.Peek();
        string described = parent.ChildDescription(description);
        int node = _nodes++;
        int scratch = ScratchFor<TCollection, TElement>(create, described);
        _scopes.Push(new ListReadScope<TCollection, TElement, TElement>(
            name, described, optional, parent.ChildRepetition, node, _columns, elementOptional, create, scratch, static element => element));
        return this;
    }

    /// <summary>Begins describing a collection of <see cref="Nullable{T}"/>s, until <see cref="End"/>, with the element described inside.</summary>
    /// <param name="elementOptional">Whether an element can be null.</param>
    /// <param name="create">Creates the collection to add the elements to; null for an array, a <see cref="List{T}"/> or an interface that one implements, which are made from what was read.</param>
    public ReadMappingBuilder<TRow> BeginNullableList<TCollection, TElement>(string? name, string? description, bool optional, bool elementOptional, Func<ICollection<TElement?>>? create = null)
        where TCollection : class
        where TElement : struct
    {
        var parent = _scopes.Peek();
        string described = parent.ChildDescription(description);
        int node = _nodes++;
        int scratch = ScratchFor<TCollection, TElement?>(create, described);
        _scopes.Push(new ListReadScope<TCollection, TElement?, TElement>(
            name, described, optional, parent.ChildRepetition, node, _columns, elementOptional, create, scratch, static element => element));
        return this;
    }

    /// <summary>Begins describing a dictionary, until <see cref="End"/>, with the key and then the value described inside.</summary>
    /// <param name="valueOptional">Whether a value can be null.</param>
    /// <param name="create">Creates the dictionary to add the entries to.</param>
    public ReadMappingBuilder<TRow> BeginMap<TMap, TKey, TValue>(string? name, string? description, bool optional, bool valueOptional, Func<IDictionary<TKey, TValue>> create)
        where TMap : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(create);
        var parent = _scopes.Peek();
        _scopes.Push(new MapReadScope<TMap, TKey, TValue, TValue>(
            name, parent.ChildDescription(description), optional, parent.ChildRepetition, _nodes++, _columns, valueOptional, create, static value => value));
        return this;
    }

    /// <summary>Begins describing a dictionary whose values are <see cref="Nullable{T}"/>s, until <see cref="End"/>, with the key and then the value described inside.</summary>
    /// <param name="valueOptional">Whether a value can be null.</param>
    /// <param name="create">Creates the dictionary to add the entries to.</param>
    public ReadMappingBuilder<TRow> BeginMapOfNullable<TMap, TKey, TValue>(string? name, string? description, bool optional, bool valueOptional, Func<IDictionary<TKey, TValue?>> create)
        where TMap : class
        where TKey : notnull
        where TValue : struct
    {
        ArgumentNullException.ThrowIfNull(create);
        var parent = _scopes.Peek();
        _scopes.Push(new MapReadScope<TMap, TKey, TValue?, TValue>(
            name, parent.ChildDescription(description), optional, parent.ChildRepetition, _nodes++, _columns, valueOptional, create, static value => value));
        return this;
    }

    /// <summary>Ends the object, collection or dictionary that was begun last.</summary>
    public ReadMappingBuilder<TRow> End()
    {
        if (_scopes.Count < 2)
        {
            throw new InvalidOperationException("End was called with nothing begun.");
        }

        var scope = _scopes.Pop();
        _scopes.Peek().Accept(scope.Name, scope.Description, scope.Optional, scope.Finish(_columns - scope.FirstColumn));
        return this;
    }

    /// <exception cref="InvalidOperationException">Something was begun and not ended, or no column was described.</exception>
    public ReadMapping<TRow> Build()
    {
        if (_scopes.Count != 1)
        {
            throw new InvalidOperationException("Something was begun and not ended.");
        }

        if (_columns == 0)
        {
            throw new InvalidOperationException($"{typeof(TRow).Name} has no columns to read.");
        }

        var root = (StructAssembler<TRow>)_scopes.Peek().Finish(_columns);
        return new ReadMapping<TRow>(new ReadModel<TRow>(root, _columns, _nodes));
    }

    /// <summary>The number of the scratch an array or list is made from, in the order the contexts are numbered (before those of what is inside); -1 when the collection is created first.</summary>
    private int ScratchFor<TCollection, TItem>(Func<ICollection<TItem>>? create, string description)
    {
        var type = typeof(TCollection);
        bool fromScratch = type.IsArray || type == typeof(List<TItem>) || (type.IsInterface && type.IsAssignableFrom(typeof(List<TItem>)));
        if (fromScratch)
        {
            return _contexts++;
        }

        return create is null
            ? throw new NotSupportedException($"{description} is a {type.Name}, which cannot be created without a function that creates it.")
            : -1;
    }
}
