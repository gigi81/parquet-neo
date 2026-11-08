using ParquetNeo.DataModel;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization;

/// <summary>
/// How rows of <typeparamref name="TRow"/> are written, found once by reflection from its public properties and cached: the
/// shredder that splits a row into the entries of the columns, and what is known of each column. A property is a column
/// unless it has <see cref="ParquetIgnoreAttribute"/>.
/// </summary>
internal sealed class RowModel<TRow>
{
    private static RowModel<TRow>? _model;

    private RowModel(StructShredder<TRow> row, IReadOnlyList<LeafInfo> columns)
    {
        Row = row;
        Columns = columns;
    }

    public StructShredder<TRow> Row { get; }

    /// <summary>The columns of the file, in order.</summary>
    public IReadOnlyList<LeafInfo> Columns { get; }

    /// <exception cref="NotSupportedException">A property has a type that cannot be written as a column.</exception>
    /// <exception cref="InvalidOperationException">The type has no properties, two share a column name, or an attribute does not suit its property.</exception>
    public static RowModel<TRow> Get() => _model ??= Create();

    public IEnumerable<SchemaNode> CreateSchemaNodes() => Row.CreateChildren();

    public ILeafBuffer[] CreateBuffers() => Columns.Select(c => c.CreateBuffer()).ToArray();

    private static RowModel<TRow> Create()
    {
        var builder = new ShredderBuilder();
        var row = builder.BuildRow<TRow>();
        return new RowModel<TRow>(row, builder.Leaves);
    }
}
