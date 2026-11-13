using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ParquetNeo.DataModel;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization;

/// <summary>
/// How rows of <typeparamref name="TRow"/> are written, found once and cached: by <see cref="Register"/>, when a mapping was
/// generated for the type at build time, and otherwise by reflection from its public properties. It holds the
/// shredder that splits a row into the entries of the columns, and what is known of each column. A property is a column
/// unless it has <see cref="ParquetIgnoreAttribute"/>.
/// </summary>
internal sealed class RowModel<TRow>
{
    private static RowModel<TRow>? _model;

    internal RowModel(StructShredder<TRow> row, IReadOnlyList<LeafInfo> columns)
    {
        Row = row;
        Columns = columns;
    }

    public StructShredder<TRow> Row { get; }

    /// <summary>The columns of the file, in order.</summary>
    public IReadOnlyList<LeafInfo> Columns { get; }

    /// <exception cref="NotSupportedException">A property has a type that cannot be written as a column.</exception>
    /// <exception cref="InvalidOperationException">The type has no properties, two share a column name, or an attribute does not suit its property.</exception>
    /// <exception cref="NotSupportedException">There is no generated mapping, and the program cannot compile code at run time (Native AOT).</exception>
    public static RowModel<TRow> Get() => _model ??= CreateByReflection();

    /// <summary>Makes a model that was generated for the type known, so that the type is never looked at by reflection.</summary>
    public static void Register(RowModel<TRow> model) => _model = model;

    public IEnumerable<SchemaNode> CreateSchemaNodes() => Row.CreateChildren();

    /// <param name="capacity">The number of entries each buffer makes room for to begin with.</param>
    public ILeafBuffer[] CreateBuffers(int capacity) => Columns.Select(c => c.CreateBuffer(capacity)).ToArray();

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCodeAttribute",
        Justification = "The reflection path is the fallback for a type without a generated mapping, and is documented as not trim safe; it is refused where code cannot be compiled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCodeAttribute",
        Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported.")]
    private static RowModel<TRow> CreateByReflection()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"{typeof(TRow).Name} has no generated mapping, and this program cannot compile the mapping at run time (Native AOT).");
        }

        var builder = new ShredderBuilder();
        var row = builder.BuildRow<TRow>();
        return new RowModel<TRow>(row, builder.Leaves);
    }
}
