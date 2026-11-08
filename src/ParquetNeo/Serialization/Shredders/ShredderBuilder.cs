using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.DataModel;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>
/// Works out, by reflection, how a row type is written: the tree of <see cref="Shredder"/>s that splits a row into the entries
/// of its columns, and the columns themselves. Done once per row type; the result is cached by <see cref="RowModel{TRow}"/>.
/// </summary>
/// <remarks>
/// A property is a column when its type is one of the primitive types, an enum (written by name), or a nested object
/// (a group of columns); a collection is a list of one of those, and a dictionary a map. Anything in the <c>System</c> namespace
/// that is not one of the primitives is refused rather than guessed at, since its public properties are not its data.
/// </remarks>
internal sealed class ShredderBuilder
{
    private readonly NullabilityInfoContext _nullability = new();
    private readonly List<LeafInfo> _leaves = [];
    private readonly Stack<Type> _objects = new();

    public IReadOnlyList<LeafInfo> Leaves => _leaves;

    /// <summary>The root: the row, whose properties are the columns at the top of the schema.</summary>
    public StructShredder<TRow> BuildRow<TRow>() => BuildObject<TRow>(false, new Position(0, 0), "", $"Row type '{typeof(TRow).Name}'");

    private StructShredder<TValue> BuildObject<TValue>(bool optional, Position position, string path, string description)
    {
        if (_objects.Contains(typeof(TValue)))
        {
            throw new NotSupportedException($"{description} contains itself, which a file with a fixed schema cannot hold.");
        }

        _objects.Push(typeof(TValue));
        try
        {
            int first = _leaves.Count;
            var inside = new Position(optional ? position.Definition + 1 : position.Definition, position.Repetition);
            var properties = Discover(typeof(TValue));
            if (properties.Count == 0)
            {
                throw new InvalidOperationException($"{typeof(TValue).Name} has no public properties to write as columns.");
            }

            var shredders = new List<PropertyShredder<TValue>>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                string name = property.GetCustomAttribute<ParquetColumnAttribute>()?.Name ?? property.Name;
                if (!names.Add(name))
                {
                    throw new InvalidOperationException($"Two properties of {typeof(TValue).Name} are written to a column named '{name}'. Rename one with {nameof(ParquetColumnAttribute)} or leave it out with {nameof(ParquetIgnoreAttribute)}.");
                }

                shredders.Add(BuildProperty<TValue>(property, name, inside, path));
            }

            return new StructShredder<TValue>(optional, first, _leaves.Count - first, description, shredders.ToArray());
        }
        finally
        {
            _objects.Pop();
        }
    }

    /// <summary>The public readable properties of an object that are written, in the order of their columns.</summary>
    internal static List<PropertyInfo> Discover(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<ParquetIgnoreAttribute>() is null)
            .OrderBy(p => p.GetCustomAttribute<ParquetColumnAttribute>()?.Order ?? 0)
            .ThenBy(p => Depth(p.DeclaringType!))
            .ThenBy(p => p.MetadataToken)
            .ToList();

    private static int Depth(Type type)
    {
        int depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private PropertyShredder<TParent> BuildProperty<TParent>(PropertyInfo property, string name, Position position, string parentPath)
    {
        var nullable = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = nullable ?? property.PropertyType;
        var nullability = _nullability.Create(property);
        bool optional = nullable is not null || (!valueType.IsValueType && nullability.ReadState != NullabilityState.NotNull);
        string path = parentPath.Length == 0 ? name : $"{parentPath}.{name}";
        var encoding = property.GetCustomAttribute<ParquetEncodingAttribute>()?.Encoding;

        var value = BuildValue(valueType, nullability, optional, property, position, path, encoding, PropertyColumns.Describe(property));
        var create = nullable is not null ? nameof(CreateNullableProperty) : nameof(CreateProperty);
        return (PropertyShredder<TParent>)Call(create, [typeof(TParent), valueType], property, name, value);
    }

    private static PropertyShredder<TParent> CreateProperty<TParent, TValue>(PropertyInfo property, string name, Shredder<TValue> value) =>
        new PropertyShredder<TParent, TValue>(name, Getters.Direct<TParent, TValue>(property), value);

    private static PropertyShredder<TParent> CreateNullableProperty<TParent, TValue>(PropertyInfo property, string name, Shredder<TValue> value)
        where TValue : struct =>
        new PropertyShredder<TParent, TValue>(name, Getters.OfNullable<TParent, TValue>(property), value);

    /// <summary>The shredder for values of a type, whatever kind of column or columns it is written as.</summary>
    private Shredder BuildValue(Type type, NullabilityInfo? nullability, bool optional, PropertyInfo source, Position position, string path, ParquetEncoding? encoding, string description, bool lenient = false)
    {
        if (ColumnTypeRegistry.IsSupported(type))
        {
            return (Shredder)Call(nameof(BuildLeaf), [type], source, lenient, optional, position, path, encoding, description);
        }

        if (type.Namespace is { } space && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal) || space.StartsWith("Microsoft.", StringComparison.Ordinal))
            && !type.IsArray && !TryGetDictionary(type, out _, out _) && !TryGetItem(type, out _))
        {
            throw new NotSupportedException($"{description} is a {type}, which cannot be written as a column.");
        }

        if (TryGetDictionary(type, out var keyType, out var valueItemType))
        {
            return BuildMap(type, keyType!, valueItemType!, nullability, optional, source, position, path, encoding, description, lenient);
        }

        if (TryGetItem(type, out var itemType))
        {
            return BuildList(type, itemType!, nullability, optional, source, position, path, encoding, description, lenient);
        }

        if (!lenient)
        {
            PropertyColumns.RejectColumnAttributes(source, type);
        }

        return (Shredder)Call(nameof(BuildObject), [type], optional, position, path, description);
    }

    private Shredder BuildLeaf<TValue>(PropertyInfo source, bool lenient, bool optional, Position position, string path, ParquetEncoding? encoding, string description)
    {
        var columnType = PropertyColumns.For<TValue>(source, lenient);
        int column = _leaves.Count;
        int maxDefinition = optional ? position.Definition + 1 : position.Definition;
        bool definitionLevels = maxDefinition > 0;
        bool repetitionLevels = position.Repetition > 0;
        _leaves.Add(new LeafInfo(path, encoding, () => new LeafBuffer<TValue>(definitionLevels, repetitionLevels, columnType)));
        return new LeafShredder<TValue>(column, optional, columnType, description);
    }

    private Shredder BuildList(Type collection, Type itemType, NullabilityInfo? nullability, bool optional, PropertyInfo source, Position position, string path, ParquetEncoding? encoding, string description, bool lenient)
    {
        RequireClass(collection, description);
        var nullableElement = Nullable.GetUnderlyingType(itemType);
        var elementType = nullableElement ?? itemType;
        var itemNullability = nullability?.ElementType ?? (nullability?.GenericTypeArguments is { Length: 1 } arguments ? arguments[0] : null);
        bool elementOptional = nullableElement is not null || (!elementType.IsValueType && itemNullability?.ReadState != NullabilityState.NotNull);

        int first = _leaves.Count;
        var inside = new Position((optional ? position.Definition + 1 : position.Definition) + 1, position.Repetition + 1);
        var element = BuildValue(elementType, itemNullability, elementOptional, source, inside, $"{path}.list.element", encoding, $"{description}, an element,", lenient);
        object reader = Call(nullableElement is not null ? nameof(NullableItemReader) : nameof(DirectItemReader), [elementType]);
        return (Shredder)Call(nameof(CreateList), [collection, itemType, elementType], optional, first, _leaves.Count - first, description, position.Repetition + 1, reader, element);
    }

    private static ListShredder<TCollection, TItem, TElement> CreateList<TCollection, TItem, TElement>(bool optional, int first, int count, string description, int repetitionLevel, ItemReader<TItem, TElement> reader, Shredder<TElement> element)
        where TCollection : class, IEnumerable<TItem> =>
        new ListShredder<TCollection, TItem, TElement>(optional, first, count, description, repetitionLevel, reader, element);

    private Shredder BuildMap(Type map, Type keyType, Type valueItemType, NullabilityInfo? nullability, bool optional, PropertyInfo source, Position position, string path, ParquetEncoding? encoding, string description, bool lenient)
    {
        RequireClass(map, description);
        if (Nullable.GetUnderlyingType(keyType) is not null)
        {
            throw new NotSupportedException($"{description} has keys of type {keyType}, but the keys of a map cannot be null.");
        }

        var nullableValue = Nullable.GetUnderlyingType(valueItemType);
        var valueType = nullableValue ?? valueItemType;
        var valueNullability = nullability?.GenericTypeArguments is { Length: 2 } arguments ? arguments[1] : null;
        bool valueOptional = nullableValue is not null || (!valueType.IsValueType && valueNullability?.ReadState != NullabilityState.NotNull);

        int first = _leaves.Count;
        var inside = new Position((optional ? position.Definition + 1 : position.Definition) + 1, position.Repetition + 1);
        if (!lenient)
        {
            PropertyColumns.RejectUnusedAttributes(source, LeafTypes(map).ToArray());
        }

        var key = BuildValue(keyType, null, false, source, inside, $"{path}.key_value.key", encoding, $"{description}, a key,", lenient: true);
        var value = BuildValue(valueType, valueNullability, valueOptional, source, inside, $"{path}.key_value.value", encoding, $"{description}, a value,", lenient: true);
        object reader = Call(nullableValue is not null ? nameof(NullableItemReader) : nameof(DirectItemReader), [valueType]);
        return (Shredder)Call(nameof(CreateMap), [map, keyType, valueItemType, valueType], optional, first, _leaves.Count - first, description, position.Repetition + 1, key, reader, value);
    }

    private static MapShredder<TMap, TKey, TItem, TValue> CreateMap<TMap, TKey, TItem, TValue>(bool optional, int first, int count, string description, int repetitionLevel, Shredder<TKey> key, ItemReader<TItem, TValue> reader, Shredder<TValue> value)
        where TKey : notnull
        where TMap : class, IEnumerable<KeyValuePair<TKey, TItem>> =>
        new MapShredder<TMap, TKey, TItem, TValue>(optional, first, count, description, repetitionLevel, key, reader, value);

    private static ItemReader<TValue, TValue> DirectItemReader<TValue>() => Getters.DirectItem<TValue>();

    private static ItemReader<TValue?, TValue> NullableItemReader<TValue>()
        where TValue : struct => Getters.NullableItem<TValue>();

    /// <summary>The primitive types a type is made of, looking through collections and dictionaries but not into objects, whose properties have attributes of their own.</summary>
    private static IEnumerable<Type> LeafTypes(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ColumnTypeRegistry.IsSupported(type))
        {
            yield return type;
        }
        else if (TryGetDictionary(type, out var key, out var value))
        {
            foreach (var leaf in LeafTypes(key!).Concat(LeafTypes(value!)))
            {
                yield return leaf;
            }
        }
        else if (TryGetItem(type, out var item))
        {
            foreach (var leaf in LeafTypes(item!))
            {
                yield return leaf;
            }
        }
    }

    private static void RequireClass(Type type, string description)
    {
        if (type.IsValueType)
        {
            throw new NotSupportedException($"{description} is a {type}, a collection that is a struct, which is not supported. Use an array, a List<T> or an interface.");
        }
    }

    /// <summary>Whether the type is a dictionary: <see cref="IDictionary{TKey, TValue}"/> or <see cref="IReadOnlyDictionary{TKey, TValue}"/>.</summary>
    internal static bool TryGetDictionary(Type type, out Type? key, out Type? value)
    {
        var found = Implemented(type, typeof(IDictionary<,>)).Concat(Implemented(type, typeof(IReadOnlyDictionary<,>)))
            .Select(i => (i.GenericTypeArguments[0], i.GenericTypeArguments[1])).Distinct().ToList();
        (key, value) = found.Count == 1 ? found[0] : (null, null);
        return found.Count == 1;
    }

    /// <summary>Whether the type is a collection of one kind of element: an array, or something that is <see cref="IEnumerable{T}"/>.</summary>
    internal static bool TryGetItem(Type type, out Type? item)
    {
        if (type.IsArray)
        {
            bool single = type.GetArrayRank() == 1;
            item = single ? type.GetElementType() : null;
            return single;
        }

        var found = Implemented(type, typeof(IEnumerable<>)).Select(i => i.GenericTypeArguments[0]).Distinct().ToList();
        item = found.Count == 1 ? found[0] : null;
        return found.Count == 1;
    }

    private static IEnumerable<Type> Implemented(Type type, Type definition) =>
        (type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces()).Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);

    /// <summary>Calls one of the generic methods of this class with the given type arguments, and lets its exceptions through as they are.</summary>
    [SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "The generic helpers called here are this class's own private methods; the type arguments are only known at run time, so they are closed over with reflection.")]
    private object Call(string method, Type[] typeArguments, params object?[] arguments)
    {
        var generic = typeof(ShredderBuilder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!.MakeGenericMethod(typeArguments);
        try
        {
            return generic.Invoke(generic.IsStatic ? null : this, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
