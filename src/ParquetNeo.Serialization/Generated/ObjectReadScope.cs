using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

internal sealed class ObjectReadScope<TValue>(string? name, string description, bool optional, int repetition, int node, int firstColumn, AssembleRow<TValue> assemble)
    : ReadScope(name, description, optional, repetition, node, firstColumn)
{
    private readonly List<IPropertyAssembler> _properties = [];
    private readonly List<Assembler> _children = [];

    public override int ChildRepetition => Repetition;

    public override string ChildDescription(string? given) => given ?? throw new InvalidOperationException($"A property of {Description} was described without a description.");

    public override void Accept(string? childName, string childDescription, bool childOptional, Assembler child)
    {
        _properties.Add(child.CreateProperty(childName ?? throw new InvalidOperationException($"A property of {Description} was described without a name."), childDescription, childOptional, _properties.Count));
        _children.Add(child);
    }

    public override Assembler Finish(int columnCount)
    {
        var descriptions = new string?[columnCount];
        foreach (var child in _children)
        {
            descriptions[child.FirstColumn - FirstColumn] = child.Description;
        }

        var frame = new ReadFrame(FirstColumn, descriptions, _children.ToArray());
        return new StructAssembler<TValue>(
            Node,
            objectIndex: -1,
            Optional,
            FirstColumn,
            columnCount,
            Description,
            _properties.ToArray(),
            constructor: null,
            context => assemble(new RowSource(context, frame)));
    }
}
