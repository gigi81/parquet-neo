using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A property of a row: takes its value from the columns and puts it in the row.</summary>
internal abstract class PropertyAssembler<TRow>
{
    public abstract void AddColumns(List<ILeafColumn> columns);

    public abstract void Read(ILeafColumn[] columns, RowContext<TRow> context);
}
