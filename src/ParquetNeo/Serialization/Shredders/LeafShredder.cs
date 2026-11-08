using System.Runtime.CompilerServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A value that is stored in a column of its own, in the way its <see cref="ColumnType{TValue}"/> says.</summary>
internal sealed class LeafShredder<TValue>(int column, bool optional, ColumnType<TValue> columnType, string description)
    : Shredder<TValue>(optional, column, 1, description)
{
    public override void Shred(TValue value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
    {
        if (present)
        {
            Unsafe.As<LeafBuffer<TValue>>(buffers[FirstColumn]).AddValue(value, Optional ? definition + 1 : definition, repetition);
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

    public override SchemaNode CreateSchemaNode(string name) => columnType.CreateNode(name, Repetition);
}
