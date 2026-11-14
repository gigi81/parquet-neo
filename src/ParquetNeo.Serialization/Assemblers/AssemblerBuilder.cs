using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.DataModel;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>
/// Works out, by reflection, how rows of a type are built from a file: which properties are read, how an instance of each object
/// is created, and which kind of collection each collection property is. Done once per row type; the result is cached by
/// <see cref="ReadModel{TRow}"/>.
/// </summary>
/// <remarks>
/// A type with a constructor that takes nothing is created and its properties with a public setter (init-only included) are set.
/// Otherwise a public constructor whose parameters are properties is used, as for a record with a primary constructor, and the
/// properties that are not parameters are set afterwards when they can be. A property is a column when its type is one of the
/// primitive types or an enum, an object (a group of columns), a collection (a list) or a dictionary (a map).
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCodeAttribute", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("Trimming", "IL2060:Call to MakeGenericMethod can not be statically analyzed", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("Trimming", "IL2070:Argument does not satisfy DynamicallyAccessedMembers requirements", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:Return value does not satisfy DynamicallyAccessedMembers requirements", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:Argument does not satisfy DynamicallyAccessedMembers requirements", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:Argument does not satisfy DynamicallyAccessedMembers requirements", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCodeAttribute", Justification = "The builder can only be made through its constructor, which is annotated: whoever calls it has accepted that it reads types by reflection and compiles code, so everything inside is covered by that.")]
internal sealed class AssemblerBuilder
{
    private readonly NullabilityInfoContext _nullability = new();
    private readonly Stack<Type> _objects = new();
    private int _slots;
    private int _nodes;
    private int _contexts;

    /// <summary>Makes a builder, which reads types by reflection and compiles code at run time: neither works trimmed or with Native AOT.</summary>
    [RequiresUnreferencedCode("Reads the properties and constructors of the row type by reflection, which trimming can remove.")]
    [RequiresDynamicCode("Compiles the code that builds a row, and closes generic types over the types of the properties.")]
    public AssemblerBuilder()
    {
    }

    /// <summary>The number of columns the row type reads.</summary>
    public int SlotCount => _slots;

    /// <summary>The number of objects, collections and dictionaries.</summary>
    public int NodeCount => _nodes;

    /// <exception cref="NotSupportedException">A property has a type that cannot be read as a column.</exception>
    /// <exception cref="InvalidOperationException">A type cannot be created, has nothing to read, or has two properties for one column name.</exception>
    public StructAssembler<TRow> BuildRow<TRow>() => BuildStruct<TRow>(false, 0, $"Row type '{typeof(TRow).Name}'");

    private StructAssembler<TValue> BuildStruct<TValue>(bool optional, int repetition, string description)
    {
        var type = typeof(TValue);
        if (_objects.Contains(type))
        {
            throw new NotSupportedException($"{description} contains itself, which a file with a fixed schema cannot hold.");
        }

        _objects.Push(type);
        try
        {
            int node = _nodes++;
            int first = _slots;
            var discovered = ShredderBuilder.Discover(type);
            var settable = discovered.Where(p => p.SetMethod is { IsPublic: true }).ToList();

            bool direct = (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) is { IsPublic: true }) || (type.IsValueType && settable.Count > 0);
            ConstructorInfo? constructor = null;
            List<PropertyInfo> properties;
            if (direct)
            {
                properties = settable;
            }
            else
            {
                constructor = ChooseConstructor(type, discovered);
                var parameters = constructor.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                properties = discovered.Where(p => parameters.Contains(p.Name) || p.SetMethod is { IsPublic: true }).ToList();
            }

            if (properties.Count == 0)
            {
                throw new InvalidOperationException($"{type.Name} has no public properties with a setter to read into.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var duplicate = properties.Find(property => !names.Add(ColumnName(property)));
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Two properties of {type.Name} are read from a column named '{ColumnName(duplicate)}'. Rename one with {nameof(ParquetColumnAttribute)} or leave it out with {nameof(ParquetIgnoreAttribute)}.");
            }

            // An object built by a constructor keeps the values waiting in a context of the read, which is numbered, like the
            // collections' scratch, in depth-first order: before those of the properties.
            int context = direct ? -1 : _contexts++;
            var factory = direct ? null : CreateConstructorFactory<TValue>(constructor!, properties);
            var assemblers = properties.Select((property, index) => CreateProperty(property, index, repetition)).ToArray();
            return new StructAssembler<TValue>(node, context, optional, first, _slots - first, description, assemblers, factory, direct ? CompileDirect<TValue>(assemblers) : null);
        }
        finally
        {
            _objects.Pop();
        }
    }

    /// <summary>The creation of an object with its parameterless constructor and the setting of every property in one function generated for the type, instead of a loop over the properties that each call into their column.</summary>
    private static Func<ReadContext, TValue> CompileDirect<TValue>(IPropertyAssembler[] properties)
    {
        var context = Expression.Parameter(typeof(ReadContext), "context");
        var row = Expression.Variable(typeof(TValue), "row");
        Expression create = typeof(TValue).IsValueType ? Expression.Default(typeof(TValue)) : Expression.New(typeof(TValue));
        var steps = new List<Expression> { Expression.Assign(row, create) };
        steps.AddRange(properties.Select(property => property.EmitFlat(row, context)));
        steps.Add(row);
        return Expression.Lambda<Func<ReadContext, TValue>>(Expression.Block([row], steps), context).Compile();
    }

    private static string ColumnName(PropertyInfo property) => property.GetCustomAttribute<ParquetColumnAttribute>()?.Name ?? property.Name;

    private static ConstructorInfo ChooseConstructor(Type type, List<PropertyInfo> properties)
    {
        ConstructorInfo? best = null;
        foreach (var candidate in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = candidate.GetParameters();
            if (parameters.Length > 0 && parameters.All(p => properties.Any(q => string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase) && q.PropertyType == p.ParameterType)))
            {
                best = candidate;
                break;
            }
        }

        return best ?? throw new InvalidOperationException(
            $"{type.Name} has no public constructor without parameters and none whose parameters are all properties of the same names and types, so rows cannot be created.");
    }

    private static ConstructorRowFactory<TValue> CreateConstructorFactory<TValue>(ConstructorInfo constructor, List<PropertyInfo> properties)
    {
        var slots = Expression.Parameter(typeof(object[]), "slots");
        var row = Expression.Variable(typeof(TValue), "row");
        Expression Slot(int index, Type type) => Expression.Convert(Expression.ArrayIndex(slots, Expression.Constant(index)), type);

        var parameterList = constructor.GetParameters();
        var arguments = parameterList
            .Select((p, i) => Slot(properties.FindIndex(q => string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase)), parameterList[i].ParameterType))
            .ToArray();
        var parameterNames = parameterList.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = new List<Expression> { Expression.Assign(row, Expression.New(constructor, arguments)) };
        for (int i = 0; i < properties.Count; i++)
        {
            if (!parameterNames.Contains(properties[i].Name) && properties[i].SetMethod is { IsPublic: true })
            {
                body.Add(Expression.Assign(Expression.Property(row, properties[i]), Slot(i, properties[i].PropertyType)));
            }
        }

        body.Add(row);
        var build = Expression.Lambda<Func<object?[], TValue>>(Expression.Block([row], body), slots).Compile();
        return new ConstructorRowFactory<TValue>(properties.Count, build);
    }

    private IPropertyAssembler CreateProperty(PropertyInfo property, int slot, int repetition)
    {
        var nullable = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = nullable ?? property.PropertyType;
        var nullability = _nullability.Create(property);
        bool optional = nullable is not null || (!valueType.IsValueType && nullability.ReadState != NullabilityState.NotNull);
        string description = PropertyColumns.Describe(property);
        var value = BuildValue(valueType, nullability, optional, repetition, description);
        return (IPropertyAssembler)Call(nameof(CreateTypedProperty), [valueType], property, slot, optional, description, value);
    }

    private static PropertyAssembler<TValue> CreateTypedProperty<TValue>(PropertyInfo property, int slot, bool optional, string description, Assembler<TValue> value) =>
        new(ColumnName(property), description, optional, slot, value, property);

    /// <summary>The assembler for values of a type, whatever kind of column or columns it is read from.</summary>
    private Assembler BuildValue(Type type, NullabilityInfo? nullability, bool optional, int repetition, string description)
    {
        if (ColumnTypeRegistry.IsSupported(type))
        {
            int slot = _slots++;
            return (Assembler)typeof(LeafAssembler<>).MakeGenericType(type).GetConstructors()[0].Invoke([slot, repetition, description]);
        }

        if (type.Namespace is { } space && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal) || space.StartsWith("Microsoft.", StringComparison.Ordinal))
            && !type.IsArray && !ShredderBuilder.TryGetDictionary(type, out _, out _) && !ShredderBuilder.TryGetItem(type, out _))
        {
            throw new NotSupportedException($"{description} is a {type}, which cannot be read as a column.");
        }

        if (ShredderBuilder.TryGetDictionary(type, out var keyType, out var valueItemType))
        {
            return BuildMap(type, keyType!, valueItemType!, nullability, repetition, description);
        }

        if (ShredderBuilder.TryGetItem(type, out var itemType))
        {
            return BuildList(type, itemType!, nullability, repetition, description);
        }

        return (Assembler)Call(nameof(BuildStruct), [type], optional, repetition, description);
    }

    private Assembler BuildList(Type collection, Type itemType, NullabilityInfo? nullability, int repetition, string description)
    {
        RequireClass(collection, description);
        var nullableElement = Nullable.GetUnderlyingType(itemType);
        var elementType = nullableElement ?? itemType;
        var itemNullability = nullability?.ElementType ?? (nullability?.GenericTypeArguments is { Length: 1 } arguments ? arguments[0] : null);
        bool elementOptional = nullableElement is not null || (!elementType.IsValueType && itemNullability?.ReadState != NullabilityState.NotNull);

        int node = _nodes++;

        // An array or a List<T> is made from the elements collected in a scratch of its own, which is one more object of the
        // context; it comes before those of the element, as the objects are numbered in depth-first order.
        int scratch = UsesScratch(collection, itemType) ? _contexts++ : -1;
        int first = _slots;
        var element = BuildValue(elementType, itemNullability, elementOptional, repetition + 1, $"{description}, an element,");
        return (Assembler)Call(nameof(CreateList), [collection, itemType, elementType], node, first, _slots - first, description, elementOptional, scratch, element);
    }

    /// <summary>Whether a collection is an array or a <see cref="List{T}"/> (or an interface that one is made for), which are made from a scratch of the elements.</summary>
    private static bool UsesScratch(Type collection, Type itemType)
    {
        var list = typeof(List<>).MakeGenericType(itemType);
        return collection.IsArray || collection == list || (collection.IsInterface && collection.IsAssignableFrom(list));
    }

    private static Assembler CreateList<TCollection, TItem, TElement>(int node, int first, int count, string description, bool elementOptional, int scratch, Assembler<TElement> element)
        where TCollection : class
    {
        var type = typeof(TCollection);

        // An array or a List<T> is made from the scratch; any other collection is created first and added to.
        Func<ICollection<TItem>>? create = null;
        if (scratch < 0)
        {
            if (type.IsInterface)
            {
                create = type.IsAssignableFrom(typeof(HashSet<TItem>))
                    ? () => new HashSet<TItem>()
                    : throw new NotSupportedException($"{description} is a {type.Name}, an interface that cannot be created. Use an array, a List<T>, a HashSet<T> or an interface they implement.");
            }
            else if (typeof(ICollection<TItem>).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is { IsPublic: true })
            {
                create = Expression.Lambda<Func<ICollection<TItem>>>(Expression.Convert(Expression.New(type), typeof(ICollection<TItem>))).Compile();
            }
            else
            {
                throw new NotSupportedException($"{description} is a {type.Name}, which cannot be created: it needs a public constructor without parameters and to be an ICollection<T>.");
            }
        }

        return new ListAssembler<TCollection, TItem, TElement>(node, first, count, description, elementOptional, create, type.IsArray, scratch, Wrapper<TElement, TItem>(), element);
    }

    private Assembler BuildMap(Type map, Type keyType, Type valueItemType, NullabilityInfo? nullability, int repetition, string description)
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

        int node = _nodes++;
        int first = _slots;
        var key = BuildValue(keyType, null, false, repetition + 1, $"{description}, a key,");
        var value = BuildValue(valueType, valueNullability, valueOptional, repetition + 1, $"{description}, a value,");
        return (Assembler)Call(nameof(CreateMap), [map, keyType, valueItemType, valueType], node, first, _slots - first, description, valueOptional, key, value);
    }

    private static Assembler CreateMap<TMap, TKey, TItem, TValue>(int node, int first, int count, string description, bool valueOptional, Assembler<TKey> key, Assembler<TValue> value)
        where TMap : class
        where TKey : notnull
    {
        var type = typeof(TMap);
        Func<IDictionary<TKey, TItem>> create;
        if (type.IsInterface)
        {
            create = type.IsAssignableFrom(typeof(Dictionary<TKey, TItem>))
                ? () => new Dictionary<TKey, TItem>()
                : throw new NotSupportedException($"{description} is a {type.Name}, an interface that cannot be created. Use a Dictionary<K, V> or an interface it implements.");
        }
        else if (typeof(IDictionary<TKey, TItem>).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is { IsPublic: true })
        {
            create = Expression.Lambda<Func<IDictionary<TKey, TItem>>>(Expression.Convert(Expression.New(type), typeof(IDictionary<TKey, TItem>))).Compile();
        }
        else
        {
            throw new NotSupportedException($"{description} is a {type.Name}, which cannot be created: it needs a public constructor without parameters and to be an IDictionary<K, V>.");
        }

        return new MapAssembler<TMap, TKey, TItem, TValue>(node, first, count, description, valueOptional, create, Wrapper<TValue, TItem>(), key, value);
    }

    /// <summary>The conversion from an element that was read to what a collection holds: nothing, or into a <see cref="Nullable{T}"/>.</summary>
    private static ItemWrapper<TElement, TItem> Wrapper<TElement, TItem>()
    {
        if (typeof(TElement) == typeof(TItem))
        {
            return (ItemWrapper<TElement, TItem>)(object)new ItemWrapper<TElement, TElement>(element => element);
        }

        var parameter = Expression.Parameter(typeof(TElement), "element");
        return Expression.Lambda<ItemWrapper<TElement, TItem>>(Expression.Convert(parameter, typeof(TItem)), parameter).Compile();
    }

    private static void RequireClass(Type type, string description)
    {
        if (type.IsValueType)
        {
            throw new NotSupportedException($"{description} is a {type}, a collection that is a struct, which is not supported. Use an array, a List<T> or an interface.");
        }
    }

    /// <summary>Calls one of the generic methods of this class with the given type arguments, and lets its exceptions through as they are.</summary>
    [SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "The generic helpers called here are this class's own private methods; the type arguments are only known at run time, so they are closed over with reflection.")]
    private object Call(string method, Type[] typeArguments, params object?[] arguments)
    {
        var generic = typeof(AssemblerBuilder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!.MakeGenericMethod(typeArguments);
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
