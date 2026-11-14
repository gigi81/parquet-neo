using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

internal sealed class ListReadScope<TCollection, TItem, TElement>(
    string? name,
    string description,
    bool optional,
    int repetition,
    int node,
    int firstColumn,
    bool elementOptional,
    Func<ICollection<TItem>>? create,
    int scratch,
    ItemWrapper<TElement, TItem> wrap) : ReadScope(name, description, optional, repetition, node, firstColumn)
    where TCollection : class
{
    private Assembler<TElement>? _element;

    public override int ChildRepetition => Repetition + 1;

    public override string ChildDescription(string? given) => $"{Description}, an element,";

    public override void Accept(string? childName, string childDescription, bool childOptional, Assembler child) => _element = (Assembler<TElement>)child;

    public override Assembler Finish(int columnCount) =>
        new ListAssembler<TCollection, TItem, TElement>(
            Node,
            FirstColumn,
            columnCount,
            Description,
            elementOptional,
            create,
            typeof(TCollection).IsArray,
            scratch,
            wrap,
            _element ?? throw new InvalidOperationException($"{Description} was described without an element."));
}
