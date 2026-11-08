using System.Linq.Expressions;
using System.Reflection;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>
/// Works out, by reflection, how rows of a type are built from a file: which properties are read, from which columns, and how
/// an instance is created. Done once per row type; the result is cached by <see cref="ReadModel{TRow}"/>.
/// </summary>
/// <remarks>
/// A type with a constructor that takes nothing is created and its properties with a public setter (init-only included) are set.
/// Otherwise a public constructor whose parameters are properties is used, as for a record with a primary constructor, and the
/// properties that are not parameters are set afterwards when they can be.
/// </remarks>
internal sealed class AssemblerBuilder<TRow>
{
    private readonly List<ReadColumn> _columns = [];

    public IReadOnlyList<ReadColumn> Columns => _columns;

    /// <exception cref="NotSupportedException">A property has a type that cannot be read as a column.</exception>
    /// <exception cref="InvalidOperationException">The type cannot be created, has nothing to read, or two properties share a column name.</exception>
    public RowAssembler<TRow> Build()
    {
        var type = typeof(TRow);
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
        foreach (var property in properties)
        {
            string name = ColumnName(property);
            if (!names.Add(name))
            {
                throw new InvalidOperationException($"Two properties of {type.Name} are read from a column named '{name}'. Rename one with {nameof(ParquetColumnAttribute)} or leave it out with {nameof(ParquetIgnoreAttribute)}.");
            }
        }

        RowFactory<TRow> factory = direct ? CreateInstanceFactory() : CreateConstructorFactory(constructor!, properties);
        var assemblers = properties.Select((property, index) => CreateProperty(property, index, factory.UsesSlots)).ToArray();
        return new RowAssembler<TRow>(assemblers, factory);
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

    private static InstanceRowFactory<TRow> CreateInstanceFactory()
    {
        Expression create = typeof(TRow).IsValueType ? Expression.Default(typeof(TRow)) : Expression.New(typeof(TRow));
        return new InstanceRowFactory<TRow>(Expression.Lambda<Func<TRow>>(create).Compile());
    }

    private static ConstructorRowFactory<TRow> CreateConstructorFactory(ConstructorInfo constructor, List<PropertyInfo> properties)
    {
        var slots = Expression.Parameter(typeof(object[]), "slots");
        var row = Expression.Variable(typeof(TRow), "row");
        Expression Slot(int index, Type type) => Expression.Convert(Expression.ArrayIndex(slots, Expression.Constant(index)), type);

        var arguments = constructor.GetParameters()
            .Select(p => properties.FindIndex(q => string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select((index, i) => Slot(index, constructor.GetParameters()[i].ParameterType))
            .ToArray();
        var parameterNames = constructor.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = new List<Expression> { Expression.Assign(row, Expression.New(constructor, arguments)) };
        for (int i = 0; i < properties.Count; i++)
        {
            if (!parameterNames.Contains(properties[i].Name) && properties[i].SetMethod is { IsPublic: true })
            {
                body.Add(Expression.Assign(Expression.Property(row, properties[i]), Slot(i, properties[i].PropertyType)));
            }
        }

        body.Add(row);
        var build = Expression.Lambda<Func<object?[], TRow>>(Expression.Block([row], body), slots).Compile();
        return new ConstructorRowFactory<TRow>(properties.Count, build);
    }

    private PropertyAssembler<TRow> CreateProperty(PropertyInfo property, int slot, bool usesSlots)
    {
        var nullable = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = nullable ?? property.PropertyType;
        string description = ColumnShapes.Describe(property);
        if (!ColumnShapes.IsPrimitive(valueType) && !valueType.IsEnum)
        {
            throw new NotSupportedException($"{description} is a {valueType}, which cannot be read as a column. Only the simple types (numbers, text, dates and times, enums) can be read so far.");
        }

        bool optional = nullable is not null || (!valueType.IsValueType && new NullabilityInfoContext().Create(property).ReadState != NullabilityState.NotNull);
        _columns.Add(new ReadColumn(ColumnName(property), valueType, optional, description));
        var create = typeof(AssemblerBuilder<TRow>).GetMethod(nameof(CreateTypedProperty), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(valueType);
        return (PropertyAssembler<TRow>)create.Invoke(this, [property, slot, usesSlots, optional, description])!;
    }

    private PropertyAssembler<TRow> CreateTypedProperty<TValue>(PropertyInfo property, int slot, bool usesSlots, bool optional, string description)
    {
        Assembler<TValue> value = typeof(TValue).IsEnum
            ? (Assembler<TValue>)Activator.CreateInstance(typeof(EnumLeafAssembler<>).MakeGenericType(typeof(TValue)), slot)!
            : new PlainLeafAssembler<TValue>(slot);
        if (usesSlots || property.SetMethod is not { IsPublic: true })
        {
            return new PropertyAssembler<TRow, TValue>(description, optional, slot, value, null, null);
        }

        var row = Expression.Parameter(typeof(TRow).MakeByRefType(), "row");
        var input = Expression.Parameter(typeof(TValue), "value");
        var target = Expression.Property(row, property);
        var set = Expression.Lambda<Setter<TRow, TValue>>(Expression.Assign(target, Expression.Convert(input, property.PropertyType)), row, input).Compile();
        var setNull = Expression.Lambda<DefaultSetter<TRow>>(Expression.Assign(target, Expression.Default(property.PropertyType)), row).Compile();
        return new PropertyAssembler<TRow, TValue>(description, optional, slot, value, set, setNull);
    }
}
