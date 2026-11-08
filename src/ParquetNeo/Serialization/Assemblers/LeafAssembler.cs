using System.Runtime.CompilerServices;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A value that has a column of its own, read as <typeparamref name="TStored"/> and converted to <typeparamref name="TValue"/>.</summary>
internal abstract class LeafAssembler<TValue, TStored>(int slot) : Assembler<TValue>
{
    protected abstract TValue Convert(TStored stored);

    public override bool Read(ILeafColumn[] columns, out TValue value)
    {
        if (Unsafe.As<LeafColumn<TStored>>(columns[slot]).TryTake(out TStored? stored))
        {
            value = Convert(stored);
            return true;
        }

        value = default!;
        return false;
    }

    public override void AddColumns(List<ILeafColumn> columns) => columns.Add(new LeafColumn<TStored>(ColumnReaders.For<TStored>()));
}
