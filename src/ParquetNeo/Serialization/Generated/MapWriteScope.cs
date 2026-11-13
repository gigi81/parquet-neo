using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

internal sealed class MapWriteScope<TMap, TKey, TItem, TValue>(
    string? name, string path, string description, bool optional, Position outside, int firstColumn, ItemReader<TItem, TValue> reader)
    : WriteScope(name, path, description, optional, outside, firstColumn)
    where TMap : class, IEnumerable<KeyValuePair<TKey, TItem>>
    where TKey : notnull
{
    private Shredder<TKey>? _key;
    private Shredder<TValue>? _value;

    public override Position ChildPosition => new((Optional ? Outside.Definition + 1 : Outside.Definition) + 1, Outside.Repetition + 1);

    public override string ChildPath(string? childName) => _key is null ? $"{Path}.key_value.key" : $"{Path}.key_value.value";

    public override string ChildDescription(string? given) => _key is null ? $"{Description}, a key," : $"{Description}, a value,";

    public override void Accept(string? childName, Shredder child)
    {
        if (_key is null)
        {
            _key = (Shredder<TKey>)child;
        }
        else
        {
            _value = (Shredder<TValue>)child;
        }
    }

    public override Shredder Finish(int columnCount) =>
        new MapShredder<TMap, TKey, TItem, TValue>(
            Optional,
            FirstColumn,
            columnCount,
            Description,
            Outside.Repetition + 1,
            _key ?? throw new InvalidOperationException($"{Description} was described without a key."),
            reader,
            _value ?? throw new InvalidOperationException($"{Description} was described without a value."));
}
