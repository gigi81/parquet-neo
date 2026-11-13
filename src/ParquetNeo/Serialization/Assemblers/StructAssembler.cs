using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>An object: built from the columns of its properties. The row itself is one, and is always there.</summary>
/// <param name="node">The number of the node, for <see cref="ReadContext.Nodes"/>.</param>
/// <param name="objectIndex">The index of the context of this type among <see cref="ReadContext.Objects"/>.</param>
internal sealed class StructAssembler<TValue>(
    int node,
    int objectIndex,
    bool optional,
    int firstColumn,
    int columnCount,
    string description,
    IPropertyAssembler[] properties,
    ConstructorRowFactory<TValue>? constructor,
    Func<ReadContext, TValue>? build) : Assembler<TValue>(firstColumn, columnCount, description)
{
    /// <summary>
    /// Builds the object from the columns: for an object created with its parameterless constructor, one function generated for the
    /// type that creates it and sets every property, instead of a loop over the properties that each call into their column; for an
    /// object built by a constructor with parameters (a record, for example), the properties are read into slots, and the
    /// constructor is called with them. The function of the first kind is <paramref name="build"/>, compiled for the type or written
    /// for it by the source generator.
    /// </summary>
    private readonly Func<ReadContext, TValue> _build = constructor is { } slots
        ? context => ReadWithConstructor(context, properties, slots, objectIndex)
        : build ?? throw new ArgumentException("An object built without a constructor needs the function that builds it.", nameof(build));

    public override bool Read(ReadContext context, out TValue value)
    {
        var binding = context.Nodes[node];
        if (binding.Controller < 0)
        {
            // The file has nothing of this object: it is null, unless it must be there, and then it has only default values.
            if (optional)
            {
                value = default!;
                return false;
            }
        }
        else if (context.Columns[binding.Controller].Definition < binding.PresentDefinition)
        {
            SkipAll(context);
            value = default!;
            return false;
        }

        value = ReadPresent(context);
        return true;
    }

    /// <summary>Builds the next row.</summary>
    public TValue ReadRow(ReadContext context)
    {
        var row = ReadPresent(context);
        context.Row++;
        return row;
    }

    /// <summary>Matches the root of the file, whose children are the columns at the top, to the row type.</summary>
    public void BindRow(GroupNode root, ReadBinder binder) => BindProperties(root, "The file", binder);

    public override void Bind(SchemaNode? file, ReadBinder binder) => Bind(file, binder, repeatedIsFine: false);

    public override void BindElement(SchemaNode? file, ReadBinder binder) => Bind(file, binder, repeatedIsFine: true);

    private void Bind(SchemaNode? file, ReadBinder binder, bool repeatedIsFine)
    {
        switch (file)
        {
            case null:
                BindProperties(null, "", binder);
                return;
            case PrimitiveNode column:
                throw ReadBinder.Mismatch($"The column '{string.Join('.', column.Path)}' of the file, for {Description}, is a single column, but the property is an object.");
            case GroupNode { Repetition: FieldRepetitionType.Repeated } repeated when !repeatedIsFine:
                throw ReadBinder.Mismatch($"The group '{string.Join('.', repeated.Path)}' of the file, for {Description}, is repeated, but the property is a single object; use a collection.");
            case GroupNode { LogicalType.Kind: LogicalTypeKind.List or LogicalTypeKind.Map } collection:
                throw ReadBinder.Mismatch($"The group '{string.Join('.', collection.Path)}' of the file, for {Description}, is a {collection.LogicalType!.Kind.ToString().ToUpperInvariant()}, but the property is an object.");
            case GroupNode group:
                BindProperties(group, $"The group '{string.Join('.', group.Path)}' of the file", binder);
                binder.BindNode(node, FirstColumn, ColumnCount, group.MaxDefinitionLevel, 0, 0);
                return;
        }
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects)
    {
        if (constructor is not null)
        {
            objects.Add(new RowContext());
        }

        foreach (var property in properties)
        {
            property.AddState(columns, objects);
        }
    }

    private void BindProperties(GroupNode? group, string where, ReadBinder binder)
    {
        foreach (var property in properties)
        {
            property.Bind(group, where, binder);
        }

        if (group is null)
        {
            binder.BindNode(node, FirstColumn, ColumnCount, 0, 0, 0);
        }
    }

    private TValue ReadPresent(ReadContext context) => _build(context);

    private static TValue ReadWithConstructor(ReadContext context, IPropertyAssembler[] properties, ConstructorRowFactory<TValue> constructor, int objectIndex)
    {
        var current = (RowContext)context.Objects[objectIndex];
        constructor.Begin(current);
        foreach (var property in properties)
        {
            property.Read(context, current);
        }

        return constructor.End(current);
    }
}
