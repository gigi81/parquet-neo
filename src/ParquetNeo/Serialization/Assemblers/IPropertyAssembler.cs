using System.Linq.Expressions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A property of an object: takes its value from the columns and puts it in the object.</summary>
internal interface IPropertyAssembler
{
    /// <summary>Matches the property to a child of the group of the file the object is read from, or to nothing when there is no such group.</summary>
    /// <param name="group">Null when the object itself is missing from the file.</param>
    /// <param name="where">Says which group it is, for messages.</param>
    void Bind(GroupNode? group, string where, ReadBinder binder);

    void AddState(List<ILeafColumn> columns, List<object> objects);

    /// <summary>Reads the property into its slot among the values that wait for the constructor of an object built by one.</summary>
    void Read(ReadContext context, RowContext parent);

    /// <summary>
    /// The code that reads the property into <paramref name="row"/>, an instance of the object that was created with its
    /// parameterless constructor, from <paramref name="context"/>: the code a <see cref="StructAssembler{TValue}"/> of such an
    /// object is made of.
    /// </summary>
    Expression EmitFlat(ParameterExpression row, ParameterExpression context);
}
