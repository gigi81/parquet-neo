using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

internal sealed class PropertyShredder<TParent, TValue>(string name, TryGetter<TParent, TValue> getter, Shredder<TValue> value)
    : PropertyShredder<TParent>(name)
{
    public override SchemaNode CreateSchemaNode() => value.CreateSchemaNode(Name);

    public override void Shred(TParent parent, int definition, int repetition, ILeafBuffer[] buffers)
    {
        bool present = getter(parent, out TValue? read);
        value.Shred(read, present, definition, repetition, buffers);
    }
}
