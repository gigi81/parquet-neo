using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization;

/// <summary>
/// How rows of <typeparamref name="TRow"/> are read, found once and cached: by <see cref="Register"/>, when a mapping was
/// generated for the type at build time, and otherwise by reflection. It holds the assembler that builds a row from
/// the columns. It is matched to the schema of each file by <see cref="Bind"/>.
/// </summary>
internal sealed class ReadModel<TRow>
{
    private static ReadModel<TRow>? _model;

    internal ReadModel(StructAssembler<TRow> row, int columnCount, int nodeCount)
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
    /// <exception cref="NotSupportedException">There is no generated mapping, and the program cannot compile code at run time (Native AOT).</exception>
    public static ReadModel<TRow> Get() => _model ??= CreateByReflection();

    /// <summary>Makes a model that was generated for the type known, so that the type is never looked at by reflection.</summary>
    public static void Register(ReadModel<TRow> model) => _model = model;

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

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCodeAttribute",
        Justification = "The reflection path is the fallback for a type without a generated mapping, and is documented as not trim safe; it is refused where code cannot be compiled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCodeAttribute",
        Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported.")]
    private static ReadModel<TRow> CreateByReflection()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"{typeof(TRow).Name} has no generated mapping, and this program cannot compile the mapping at run time (Native AOT).");
        }

        var builder = new AssemblerBuilder();
        var row = builder.BuildRow<TRow>();
        return new ReadModel<TRow>(row, builder.SlotCount, builder.NodeCount);
    }
}
