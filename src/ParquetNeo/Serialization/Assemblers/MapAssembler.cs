using ParquetNeo.Exceptions;
using ParquetNeo.Nested;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A dictionary: the entries of a MAP of the file, in the kind of dictionary the property has.</summary>
/// <typeparam name="TMap">The type of the property.</typeparam>
/// <typeparam name="TKey">The type of the keys, which are never null.</typeparam>
/// <typeparam name="TItem">The type of the values, which is <typeparamref name="TValue"/> or a <see cref="Nullable{T}"/> of it.</typeparam>
internal sealed class MapAssembler<TMap, TKey, TItem, TValue>(
    int node,
    int firstColumn,
    int columnCount,
    string description,
    bool valueOptional,
    Func<IDictionary<TKey, TItem>> createSink,
    ItemWrapper<TValue, TItem> wrap,
    Assembler<TKey> keyAssembler,
    Assembler<TValue> valueAssembler) : Assembler<TMap>(firstColumn, columnCount, description)
    where TMap : class
    where TKey : notnull
{
    public override bool Read(ReadContext context, out TMap value)
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
                if (!keyAssembler.Read(context, out TKey? readKey))
                {
                    throw new ValueConversionException($"{Description} has a null key in row {context.Row} of the file, but keys cannot be null.");
                }

                bool present = valueAssembler.Read(context, out TValue? readValue);
                if (!present && !valueOptional)
                {
                    throw new ValueConversionException($"{Description} cannot hold a null value, but row {context.Row} of the file has one.");
                }

                sink[readKey] = present ? wrap(readValue!) : default!;
            }
            while (!first.AtEnd && first.Repetition == binding.RepetitionLevel);
        }
        else
        {
            SkipAll(context);
        }

        value = (TMap)sink;
        return true;
    }

    public override void Bind(SchemaNode? file, ReadBinder binder)
    {
        if (file is null)
        {
            keyAssembler.Bind(null, binder);
            valueAssembler.Bind(null, binder);
            binder.BindNode(node, FirstColumn, ColumnCount, 0, 0, 0);
            return;
        }

        var layout = NestedSchema.GetMapLayout(file)
            ?? throw ReadBinder.Mismatch($"The {(file is GroupNode ? "group" : "column")} '{string.Join('.', file.Path)}' of the file, for {Description}, is not a map.");
        if (layout.Value is null)
        {
            throw ReadBinder.Mismatch($"The map '{string.Join('.', file.Path)}' of the file, for {Description}, has keys but no values.");
        }

        keyAssembler.Bind(layout.Key, binder);
        valueAssembler.Bind(layout.Value, binder);
        binder.BindNode(node, FirstColumn, ColumnCount, layout.Container.MaxDefinitionLevel, layout.KeyValue.MaxDefinitionLevel, layout.KeyValue.MaxRepetitionLevel);
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects)
    {
        keyAssembler.AddState(columns, objects);
        valueAssembler.AddState(columns, objects);
    }
}
