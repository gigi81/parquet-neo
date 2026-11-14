using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

internal sealed class ObjectWriteScope<TValue>(string? name, string path, string description, bool optional, Position outside, int firstColumn, ShredRow<TValue> shred)
    : WriteScope(name, path, description, optional, outside, firstColumn)
{
    private readonly List<PropertyShredder> _properties = [];
    private readonly List<Shredder> _children = [];

    public override Position ChildPosition => new(Optional ? Outside.Definition + 1 : Outside.Definition, Outside.Repetition);

    public override string ChildPath(string? childName) =>
        Path.Length == 0 ? childName ?? throw Unnamed() : $"{Path}.{childName ?? throw Unnamed()}";

    public override string ChildDescription(string? given) => given ?? throw Unnamed();

    public override void Accept(string? childName, Shredder child)
    {
        _properties.Add(child.CreateProperty(childName ?? throw Unnamed()));
        _children.Add(child);
    }

    public override Shredder Finish(int columnCount)
    {
        var descriptions = new string?[columnCount];
        foreach (var child in _children)
        {
            descriptions[child.FirstColumn - FirstColumn] = child.Description;
        }

        var frame = new ShredFrame(FirstColumn, descriptions, _children.ToArray());
        return new StructShredder<TValue>(
            Optional,
            FirstColumn,
            columnCount,
            Description,
            _properties.ToArray(),
            (row, definition, repetition, buffers) => shred(row, new RowSink(frame, buffers, definition, repetition)));
    }

    private InvalidOperationException Unnamed() => new($"A property of {Description} was described without a name.");
}
