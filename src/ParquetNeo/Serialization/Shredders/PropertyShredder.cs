using System.Linq.Expressions;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A property of an object: reads it and hands the value to the shredder of its type.</summary>
internal abstract class PropertyShredder(string name)
{
    public string Name { get; } = name;

    public abstract SchemaNode CreateSchemaNode();

    /// <summary>
    /// The code that reads the property of <paramref name="parent"/> and gives it to the columns below it: for a single column it
    /// adds the value to the buffer, otherwise it hands the value to the shredder of the property. The definition and repetition
    /// level are expressions, since an object can be anywhere in the row.
    /// </summary>
    /// <param name="parent">The object, as a parameter of the function that shreds all of its properties.</param>
    /// <param name="definition">An <see cref="int"/>: the optional and repeated ancestors that are present.</param>
    /// <param name="repetition">An <see cref="int"/>: the repetition level of the first entry.</param>
    /// <param name="buffers">The <see cref="Buffers.ILeafBuffer"/>s of the columns of the file.</param>
    public abstract Expression Emit(ParameterExpression parent, Expression definition, Expression repetition, ParameterExpression buffers);
}
