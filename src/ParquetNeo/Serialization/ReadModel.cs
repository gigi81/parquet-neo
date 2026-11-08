using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization;

/// <summary>
/// How rows of <typeparamref name="TRow"/> are read, found once by reflection and cached: the columns it needs, and the
/// assembler that builds a row from them. The columns are matched to those of a file by <see cref="Bind"/>.
/// </summary>
internal sealed class ReadModel<TRow>
{
    private static ReadModel<TRow>? _model;

    private ReadModel(RowAssembler<TRow> row, IReadOnlyList<ReadColumn> columns)
    {
        Row = row;
        Columns = columns;
    }

    public RowAssembler<TRow> Row { get; }

    public IReadOnlyList<ReadColumn> Columns { get; }

    /// <exception cref="NotSupportedException">A property has a type that cannot be read as a column.</exception>
    /// <exception cref="InvalidOperationException">The type cannot be created, has nothing to read, or two properties share a column name.</exception>
    public static ReadModel<TRow> Get() => _model ??= Create();

    public ILeafColumn[] CreateColumns()
    {
        var columns = new List<ILeafColumn>();
        Row.AddColumns(columns);
        return columns.ToArray();
    }

    /// <summary>
    /// Finds the column of the file for every column of the row type: the one of the same name or, when there is none and only one
    /// differs by case, that one. A column the file lacks is fine when the property can hold null.
    /// </summary>
    /// <returns>For each column of the row type, its index among the columns of the file, or -1 when the file has none.</returns>
    /// <exception cref="SchemaMismatchException">A column is missing for a property that cannot be null, is a group, or cannot be read as the property's type.</exception>
    public int[] Bind(ParquetSchema schema)
    {
        var found = new int[Columns.Count];
        for (int i = 0; i < Columns.Count; i++)
        {
            var wanted = Columns[i];
            var node = Find(schema, wanted.Name);
            if (node is null)
            {
                if (!wanted.Optional)
                {
                    throw new SchemaMismatchException(
                        $"The file has no column named '{wanted.Name}' for {wanted.Description}, which cannot be null. Its columns are: {string.Join(", ", schema.Root.Children.Select(c => $"'{c.Name}'"))}.");
                }

                found[i] = -1;
                continue;
            }

            if (node is not PrimitiveNode column)
            {
                throw new SchemaMismatchException($"The column '{node.Name}' of the file, for {wanted.Description}, is a group, but the property is a simple type.");
            }

            if (ColumnCompatibility.Problem(column, wanted.ValueType) is { } problem)
            {
                throw new SchemaMismatchException($"The column '{column.Name}' of the file cannot be read into {wanted.Description}: {problem}.");
            }

            found[i] = column.ColumnIndex;
        }

        return found;
    }

    private static SchemaNode? Find(ParquetSchema schema, string name)
    {
        var children = schema.Root.Children;
        var exact = children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var similar = children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return similar.Count == 1 ? similar[0] : null;
    }

    private static ReadModel<TRow> Create()
    {
        var builder = new AssemblerBuilder<TRow>();
        var row = builder.Build();
        return new ReadModel<TRow>(row, builder.Columns);
    }
}
