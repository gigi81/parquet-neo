using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>An object: a group of the properties it is written as, optional when the object may be null.</summary>
internal sealed class StructShredder<TValue>(bool optional, int firstColumn, int columnCount, string description, PropertyShredder<TValue>[] properties)
    : Shredder<TValue>(optional, firstColumn, columnCount, description)
{
    public override void Shred(TValue value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
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

        ShredProperties(value, Optional ? definition + 1 : definition, repetition, buffers);
    }

    /// <summary>Shreds the properties of an object that is there; the root, which is not a group of the file, starts here.</summary>
    public void ShredProperties(TValue value, int definition, int repetition, ILeafBuffer[] buffers)
    {
        foreach (var property in properties)
        {
            property.Shred(value, definition, repetition, buffers);
        }
    }

    public override SchemaNode CreateSchemaNode(string name) => new GroupNode(name, Repetition, CreateChildren());

    public IEnumerable<SchemaNode> CreateChildren() => properties.Select(p => p.CreateSchemaNode());
}
