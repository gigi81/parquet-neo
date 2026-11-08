using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A property of an object: reads it and hands the value to the shredder of its type.</summary>
internal abstract class PropertyShredder<TParent>(string name)
{
    public string Name { get; } = name;

    public abstract SchemaNode CreateSchemaNode();

    public abstract void Shred(TParent parent, int definition, int repetition, ILeafBuffer[] buffers);
}
