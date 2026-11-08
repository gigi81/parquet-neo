using System.Linq.Expressions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A property of an object: reads it and hands the value to the shredder of its type.</summary>
internal abstract class PropertyShredder<TParent>(string name)
{
    public string Name { get; } = name;

    public abstract SchemaNode CreateSchemaNode();

    public abstract void Shred(TParent parent, int definition, int repetition, ILeafBuffer[] buffers);

    /// <summary>
    /// The code that does what <see cref="Shred"/> does for a row at the top (no optional or repeated ancestors, so definition and
    /// repetition level 0), as an expression that reads the property of <paramref name="parent"/> and adds it to the column
    /// buffer in <paramref name="buffers"/>; or null when the property is more than a single column.
    /// </summary>
    public virtual Expression? EmitFlat(ParameterExpression parent, ParameterExpression buffers) => null;
}
