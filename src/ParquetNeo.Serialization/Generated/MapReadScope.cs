using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

internal sealed class MapReadScope<TMap, TKey, TItem, TValue>(
    string? name,
    string description,
    bool optional,
    int repetition,
    int node,
    int firstColumn,
    bool valueOptional,
    Func<IDictionary<TKey, TItem>> create,
    ItemWrapper<TValue, TItem> wrap) : ReadScope(name, description, optional, repetition, node, firstColumn)
    where TMap : class
    where TKey : notnull
{
    private Assembler<TKey>? _key;
    private Assembler<TValue>? _value;

    public override int ChildRepetition => Repetition + 1;

    public override string ChildDescription(string? given) => _key is null ? $"{Description}, a key," : $"{Description}, a value,";

    public override void Accept(string? childName, string childDescription, bool childOptional, Assembler child)
    {
        if (_key is null)
        {
            _key = (Assembler<TKey>)child;
        }
        else
        {
            _value = (Assembler<TValue>)child;
        }
    }

    public override Assembler Finish(int columnCount) =>
        new MapAssembler<TMap, TKey, TItem, TValue>(
            Node,
            FirstColumn,
            columnCount,
            Description,
            valueOptional,
            create,
            wrap,
            _key ?? throw new InvalidOperationException($"{Description} was described without a key."),
            _value ?? throw new InvalidOperationException($"{Description} was described without a value."));
}
