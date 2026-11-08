using System.Runtime.CompilerServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A value that has a column of its own, read in the way its <see cref="ColumnType{TValue}"/> says.</summary>
/// <param name="repetition">How many collections the value is in, which is how many repeated nodes its column must have.</param>
internal sealed class LeafAssembler<TValue>(int slot, int repetition, string description) : Assembler<TValue>(slot, 1, description)
{
    private readonly ColumnType<TValue> _type = ColumnTypeRegistry.Get<TValue>();

    public override bool Read(ReadContext context, out TValue value) =>
        Unsafe.As<LeafColumn<TValue>>(context.Columns[FirstColumn]).TryTake(out value!);

    public override void Bind(SchemaNode? file, ReadBinder binder)
    {
        switch (file)
        {
            case null:
                return;
            case PrimitiveNode column:
                if (Problem(column) is { } problem)
                {
                    throw ReadBinder.Mismatch($"The column '{string.Join('.', column.Path)}' of the file cannot be read into {Description}: {problem}.");
                }

                binder.BindColumn(FirstColumn, column.ColumnIndex);
                return;
            default:
                throw ReadBinder.Mismatch($"The column '{string.Join('.', file.Path)}' of the file, for {Description}, is a group, but the property is a simple type.");
        }
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects) => columns.Add(new LeafColumn<TValue>(_type));

    /// <summary>Why the column cannot be read as the type of the property, or null when it can: its repetition must match the collections the property is in, and the type must fit it.</summary>
    private string? Problem(PrimitiveNode column)
    {
        if (column.MaxRepetitionLevel != repetition)
        {
            return repetition == 0
                ? "it is repeated, and only the elements of a collection can be read from a repeated column"
                : $"it is repeated {column.MaxRepetitionLevel} times, but the property is in {repetition} collections";
        }

        return _type.GetProblem(column);
    }
}
