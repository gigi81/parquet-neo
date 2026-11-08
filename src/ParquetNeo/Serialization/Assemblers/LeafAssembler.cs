using System.Runtime.CompilerServices;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A value that has a column of its own, read as <typeparamref name="TStored"/> and converted to <typeparamref name="TValue"/>.</summary>
/// <param name="repetition">How many collections the value is in, which is how many repeated nodes its column must have.</param>
internal abstract class LeafAssembler<TValue, TStored>(int slot, int repetition, string description) : Assembler<TValue>(slot, 1, description)
{
    protected abstract TValue Convert(TStored stored);

    public override bool Read(ReadContext context, out TValue value)
    {
        if (Unsafe.As<LeafColumn<TStored>>(context.Columns[FirstColumn]).TryTake(out TStored? stored))
        {
            value = Convert(stored);
            return true;
        }

        value = default!;
        return false;
    }

    public override void Bind(SchemaNode? file, ReadBinder binder)
    {
        switch (file)
        {
            case null:
                return;
            case PrimitiveNode column:
                if (ColumnCompatibility.Problem(column, typeof(TValue), repetition) is { } problem)
                {
                    throw ReadBinder.Mismatch($"The column '{string.Join('.', column.Path)}' of the file cannot be read into {Description}: {problem}.");
                }

                binder.BindColumn(FirstColumn, column.ColumnIndex);
                return;
            default:
                throw ReadBinder.Mismatch($"The column '{string.Join('.', file.Path)}' of the file, for {Description}, is a group, but the property is a simple type.");
        }
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects) => columns.Add(new LeafColumn<TStored>(ColumnReaders.For<TStored>()));
}
