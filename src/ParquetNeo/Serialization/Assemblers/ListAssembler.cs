using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// <param name="scratchIndex">
/// The index of the <see cref="ItemScratch{TItem}"/> of this list among <see cref="ReadContext.Objects"/>, or -1 for a collection
/// that is created first and added to (anything but an array or a <see cref="List{T}"/>).
/// </param>
internal sealed class ListAssembler<TCollection, TItem, TElement>(
    int node,
    int firstColumn,
    int columnCount,
    string description,
    bool elementOptional,
    Func<ICollection<TItem>> createSink,
    bool toArray,
    int scratchIndex,
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

        if (scratchIndex >= 0)
        {
            value = ReadIntoExactCollection(context, first, binding);
            return true;
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

                sink.Add(present ? Wrapped(read!) : default!);
            }
            while (!first.AtEnd && first.Repetition == binding.RepetitionLevel);
        }
        else
        {
            SkipAll(context);
        }

        value = (TCollection)sink;
        return true;
    }

    /// <summary>
    /// Reads the elements into the scratch of the context and makes the array or <see cref="List{T}"/> from them in one piece, the
    /// size of what was read, instead of adding to a list that doubles as it fills (and copying it again for an array).
    /// </summary>
    private TCollection ReadIntoExactCollection(ReadContext context, ILeafColumn first, NodeBinding binding)
    {
        var scratch = (ItemScratch<TItem>)context.Objects[scratchIndex];
        scratch.Clear();
        if (first.Definition >= binding.ElementsDefinition)
        {
            do
            {
                bool present = element.Read(context, out TElement? read);
                if (!present && !elementOptional)
                {
                    throw new ValueConversionException($"{Description} cannot be null, but row {context.Row} of the file has a null for it.");
                }

                scratch.Add(present ? Wrapped(read!) : default!);
            }
            while (!first.AtEnd && first.Repetition == binding.RepetitionLevel);
        }
        else
        {
            SkipAll(context);
        }

        var items = scratch.Items;
        TCollection result;
        if (toArray)
        {
            var array = items.Length == 0 ? Array.Empty<TItem>() : new TItem[items.Length];
            items.CopyTo(array);
            result = (TCollection)(object)array;
        }
        else
        {
            var list = new List<TItem>(items.Length);
            CollectionsMarshal.SetCount(list, items.Length);
            items.CopyTo(CollectionsMarshal.AsSpan(list));
            result = (TCollection)(object)list;
        }

        scratch.Release();
        return result;
    }

    /// <summary>An element as the collection holds it: the same, unless the collection holds <see cref="Nullable{T}"/>s of it.</summary>
    private TItem Wrapped(TElement element) => typeof(TElement) == typeof(TItem) ? Unsafe.As<TElement, TItem>(ref element) : wrap(element);

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

    public override void AddState(List<ILeafColumn> columns, List<object> objects)
    {
        if (scratchIndex >= 0)
        {
            objects.Add(new ItemScratch<TItem>());
        }

        element.AddState(columns, objects);
    }
}
