using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

internal sealed class ListWriteScope<TCollection, TItem, TElement>(
    string? name, string path, string description, bool optional, Position outside, int firstColumn, ItemReader<TItem, TElement> reader)
    : WriteScope(name, path, description, optional, outside, firstColumn)
    where TCollection : class, IEnumerable<TItem>
{
    private Shredder<TElement>? _element;

    public override Position ChildPosition => new((Optional ? Outside.Definition + 1 : Outside.Definition) + 1, Outside.Repetition + 1);

    public override string ChildPath(string? childName) => $"{Path}.list.element";

    public override string ChildDescription(string? given) => $"{Description}, an element,";

    public override void Accept(string? childName, Shredder child) => _element = (Shredder<TElement>)child;

    public override Shredder Finish(int columnCount) =>
        new ListShredder<TCollection, TItem, TElement>(
            Optional,
            FirstColumn,
            columnCount,
            Description,
            Outside.Repetition + 1,
            reader,
            _element ?? throw new InvalidOperationException($"{Description} was described without an element."));
}
