using System.Runtime.InteropServices;
using ParquetNeo.Nested;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A collection: a LIST group of repeated elements, optional when the collection may be null.</summary>
/// <typeparam name="TCollection">The type of the property.</typeparam>
/// <typeparam name="TItem">What it enumerates, which is <typeparamref name="TElement"/> or a <see cref="Nullable{T}"/> of it.</typeparam>
/// <typeparam name="TElement">The type of the elements, without the nullability of the item.</typeparam>
internal sealed class ListShredder<TCollection, TItem, TElement>(
    bool optional,
    int firstColumn,
    int columnCount,
    string description,
    int repetitionLevel,
    ItemReader<TItem, TElement> reader,
    Shredder<TElement> element) : Shredder<TCollection>(optional, firstColumn, columnCount, description)
    where TCollection : class, IEnumerable<TItem>
{
    public override void Shred(TCollection value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
    {
        if (!present)
        {
            if (!Optional)
            {
                throw Missing();
            }

            AddMissing(buffers, definition, repetition);
            return;
        }

        int listDefinition = Optional ? definition + 1 : definition;
        bool empty = true;
        switch (value)
        {
            case TItem[] array:
                foreach (var item in array)
                {
                    ShredItem(item, empty, listDefinition, repetition, buffers);
                    empty = false;
                }

                break;
            case List<TItem> list:
                foreach (var item in CollectionsMarshal.AsSpan(list))
                {
                    ShredItem(item, empty, listDefinition, repetition, buffers);
                    empty = false;
                }

                break;
            default:
                foreach (var item in value)
                {
                    ShredItem(item, empty, listDefinition, repetition, buffers);
                    empty = false;
                }

                break;
        }

        if (empty)
        {
            AddMissing(buffers, listDefinition, repetition);
        }
    }

    public override SchemaNode CreateSchemaNode(string name) => NestedSchema.List(name, Repetition, element.CreateSchemaNode("element"));

    private void ShredItem(TItem item, bool first, int listDefinition, int repetition, ILeafBuffer[] buffers)
    {
        bool present = reader(item, out TElement? read);
        element.Shred(read, present, listDefinition + 1, first ? repetition : repetitionLevel, buffers);
    }
}
