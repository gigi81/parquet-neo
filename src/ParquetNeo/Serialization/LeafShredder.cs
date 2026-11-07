using System.Runtime.CompilerServices;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>A value that is stored in a column of its own, as <typeparamref name="TStored"/>.</summary>
internal abstract class LeafShredder<TValue, TStored>(int column, bool optional, ColumnShape shape, string description)
    : Shredder<TValue>(optional, column, 1, description)
{
    protected abstract TStored Store(TValue value);

    public override void Shred(TValue value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
    {
        if (present)
        {
            Unsafe.As<LeafBuffer<TStored>>(buffers[FirstColumn]).AddValue(Store(value), Optional ? definition + 1 : definition, repetition);
        }
        else if (Optional)
        {
            buffers[FirstColumn].AddMissing(definition, repetition);
        }
        else
        {
            throw Missing();
        }
    }

    public override SchemaNode CreateSchemaNode(string name) => new PrimitiveNode(name, shape.Type, Repetition, shape.LogicalType, shape.TypeLength);
}
