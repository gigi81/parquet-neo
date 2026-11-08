using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A property of an object: takes its value from the columns and puts it in the object.</summary>
internal abstract class PropertyAssembler<TParent>
{
    /// <summary>Matches the property to a child of the group of the file the object is read from, or to nothing when there is no such group.</summary>
    /// <param name="group">Null when the object itself is missing from the file.</param>
    /// <param name="where">Says which group it is, for messages.</param>
    public abstract void Bind(GroupNode? group, string where, ReadBinder binder);

    public abstract void AddState(List<ILeafColumn> columns, List<object> objects);

    public abstract void Read(ReadContext context, RowContext<TParent> parent);
}
