using ParquetNeo.Exceptions;
using ParquetNeo.Nested;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A collection: the elements of a LIST of the file, in the kind of collection the property has.</summary>
/// <typeparam name="TCollection">The type of the property.</typeparam>
/// <typeparam name="TItem">What the collection holds, which is <typeparamref name="TElement"/> or a <see cref="Nullable{T}"/> of it.</typeparam>
/// <param name="createSink">Creates the collection that the elements are added to.</param>
/// <param name="toArray">Whether the property is an array, which is made from the collection once it is full.</param>
internal sealed class ListAssembler<TCollection, TItem, TElement>(
    int node,
    int firstColumn,
    int columnCount,
    string description,
    bool elementOptional,
    Func<ICollection<TItem>> createSink,
    bool toArray,
    ItemWrapper<TElement, TItem> wrap,
    Assembler<TElement> element) : Assembler<TCollection>(firstColumn, columnCount, description)
    where TCollection : class
{
    public override bool Read(ReadContext context, out TCollection value)
    {
        var binding = context.Nodes[node];
        value = default!;
        if (binding.Controller < 0)
        {
            return false;
        }

        var first = context.Columns[binding.Controller];
        int definition = first.Definition;
        if (definition < binding.PresentDefinition)
        {
            SkipAll(context);
            return false;
        }

        var sink = createSink();
        if (definition >= binding.ElementsDefinition)
        {
            do
            {
                bool present = element.Read(context, out TElement? read);
                if (!present && !elementOptional)
                {
                    throw new ValueConversionException($"{Description} cannot be null, but row {context.Row} of the file has a null for it.");
                }

                sink.Add(present ? wrap(read!) : default!);
            }
            while (!first.AtEnd && first.Repetition == binding.RepetitionLevel);
        }
        else
        {
            SkipAll(context);
        }

        value = toArray ? (TCollection)(object)((List<TItem>)sink).ToArray() : (TCollection)sink;
        return true;
    }

    public override void Bind(SchemaNode? file, ReadBinder binder)
    {
        if (file is null)
        {
            element.Bind(null, binder);
            binder.BindNode(node, FirstColumn, ColumnCount, 0, 0, 0);
            return;
        }

        var layout = NestedSchema.GetListLayout(file)
            ?? throw ReadBinder.Mismatch($"The {(file is GroupNode ? "group" : "column")} '{string.Join('.', file.Path)}' of the file, for {Description}, is not a list.");
        element.BindElement(layout.Element, binder);

        // A repeated field is its own list, which cannot be null; any other list is null from the level of its LIST group.
        int present = layout.Container == layout.Repeated ? layout.Repeated.MaxDefinitionLevel - 1 : layout.Container.MaxDefinitionLevel;
        binder.BindNode(node, FirstColumn, ColumnCount, present, layout.Repeated.MaxDefinitionLevel, layout.Repeated.MaxRepetitionLevel);
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects) => element.AddState(columns, objects);
}
