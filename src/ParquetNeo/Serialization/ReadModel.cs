using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization;

/// <summary>
/// How rows of <typeparamref name="TRow"/> are read, found once by reflection and cached: the assembler that builds a row from
/// the columns. It is matched to the schema of each file by <see cref="Bind"/>.
/// </summary>
internal sealed class ReadModel<TRow>
{
    private static ReadModel<TRow>? _model;

    private ReadModel(StructAssembler<TRow> row, int columnCount, int nodeCount)
    {
        Row = row;
        ColumnCount = columnCount;
        NodeCount = nodeCount;
    }

    public StructAssembler<TRow> Row { get; }

    /// <summary>The number of columns the row type reads, which are the slots.</summary>
    public int ColumnCount { get; }

    public int NodeCount { get; }

    /// <exception cref="NotSupportedException">A property has a type that cannot be read as a column.</exception>
    /// <exception cref="InvalidOperationException">A type cannot be created, has nothing to read, or has two properties for one column name.</exception>
    public static ReadModel<TRow> Get() => _model ??= Create();

    /// <summary>
    /// Matches the row type to the schema of a file: each property to the column or group of the same name or, when there is none
    /// and only one differs by case, that one. Something the file lacks is fine when the property can hold null.
    /// </summary>
    /// <exception cref="Exceptions.SchemaMismatchException">Something is missing for a property that cannot be null, or is stored as a type the property cannot hold.</exception>
    public ReadPlan Bind(ParquetSchema schema)
    {
        var binder = new ReadBinder(ColumnCount, NodeCount);
        Row.BindRow(schema.Root, binder);
        return binder.ToPlan();
    }

    public ReadContext CreateContext(ReadPlan plan)
    {
        var columns = new List<ILeafColumn>();
        var objects = new List<object>();
        Row.AddState(columns, objects);
        return new ReadContext(columns.ToArray(), objects.ToArray(), plan.Nodes);
    }

    private static ReadModel<TRow> Create()
    {
        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<TRow>();
        return new ReadModel<TRow>(row, builder.SlotCount, builder.NodeCount);
    }
}
