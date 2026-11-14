using ParquetNeo.Nested;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A dictionary: a MAP group of repeated key and value pairs, optional when the dictionary may be null.</summary>
/// <typeparam name="TMap">The type of the property.</typeparam>
/// <typeparam name="TKey">The type of the keys, which are never null.</typeparam>
/// <typeparam name="TItem">The type of the values, which is <typeparamref name="TValue"/> or a <see cref="Nullable{T}"/> of it.</typeparam>
internal sealed class MapShredder<TMap, TKey, TItem, TValue>(
    bool optional,
    int firstColumn,
    int columnCount,
    string description,
    int repetitionLevel,
    Shredder<TKey> keyShredder,
    ItemReader<TItem, TValue> reader,
    Shredder<TValue> valueShredder) : Shredder<TMap>(optional, firstColumn, columnCount, description)
    where TKey : notnull
    where TMap : class, IEnumerable<KeyValuePair<TKey, TItem>>
{
    public override void Shred(TMap value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
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

        int mapDefinition = Optional ? definition + 1 : definition;
        bool empty = true;
        if (value is Dictionary<TKey, TItem> dictionary)
        {
            foreach (var pair in dictionary)
            {
                ShredPair(pair, empty, mapDefinition, repetition, buffers);
                empty = false;
            }
        }
        else
        {
            foreach (var pair in value)
            {
                ShredPair(pair, empty, mapDefinition, repetition, buffers);
                empty = false;
            }
        }

        if (empty)
        {
            AddMissing(buffers, mapDefinition, repetition);
        }
    }

    public override SchemaNode CreateSchemaNode(string name) => NestedSchema.Map(name, Repetition, keyShredder.CreateSchemaNode("key"), valueShredder.CreateSchemaNode("value"));

    private void ShredPair(KeyValuePair<TKey, TItem> pair, bool first, int mapDefinition, int repetition, ILeafBuffer[] buffers)
    {
        int entryRepetition = first ? repetition : repetitionLevel;
        keyShredder.Shred(pair.Key, pair.Key is not null, mapDefinition + 1, entryRepetition, buffers);
        bool present = reader(pair.Value, out TValue? read);
        valueShredder.Shred(read, present, mapDefinition + 1, entryRepetition, buffers);
    }
}
