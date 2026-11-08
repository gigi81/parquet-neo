using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Builds the rows of a row group from the columns.</summary>
internal sealed class RowAssembler<TRow>(PropertyAssembler<TRow>[] properties, RowFactory<TRow> factory)
{
    public void AddColumns(List<ILeafColumn> columns)
    {
        foreach (var property in properties)
        {
            property.AddColumns(columns);
        }
    }

    /// <summary>Takes the entries of the next row from the columns.</summary>
    public TRow Read(ILeafColumn[] columns, RowContext<TRow> context)
    {
        factory.Begin(context);
        foreach (var property in properties)
        {
            property.Read(columns, context);
        }

        context.Row++;
        return factory.End(context);
    }
}
