using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>
/// Builds a part of a row from the entries of the columns below it: the reverse of shredding. The columns that belong to it are
/// a run of slots in depth-first order.
/// </summary>
internal abstract class Assembler(int firstColumn, int columnCount, string description)
{
    public int FirstColumn { get; } = firstColumn;

    public int ColumnCount { get; } = columnCount;

    /// <summary>Says where the part is, for messages.</summary>
    public string Description { get; } = description;

    /// <summary>Matches the part to a node of the file, or to nothing when the file lacks it, and checks that the node fits.</summary>
    /// <exception cref="Exceptions.SchemaMismatchException">The node cannot be read as the part.</exception>
    public abstract void Bind(SchemaNode? file, ReadBinder binder);

    /// <summary>
    /// Matches the part to the node of the file that holds the elements of a list. That can be the repeated node itself, as in the
    /// older layouts of lists, which is not a problem for an element as it would be for a single object.
    /// </summary>
    public virtual void BindElement(SchemaNode? file, ReadBinder binder) => Bind(file, binder);

    /// <summary>Adds an empty column for each column of the part, in slot order, and an empty context for each object type.</summary>
    public abstract void AddState(List<ILeafColumn> columns, List<object> objects);

    /// <summary>Takes the entries of a part that is missing, one for each column that the file has.</summary>
    protected void SkipAll(ReadContext context)
    {
        for (int slot = FirstColumn; slot < FirstColumn + ColumnCount; slot++)
        {
            context.Columns[slot].Skip();
        }
    }
}
