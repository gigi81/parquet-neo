using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// What every column type says without knowing its .NET type: which one it is, the column it creates, and whether a column fits it.
/// Derive from <see cref="ColumnType{TValue}"/>, which adds reading and writing, to add a type of your own.
/// </summary>
public abstract class ColumnType
{
    internal ColumnType()
    {
    }

    /// <summary>The name of the .NET type, as messages call it.</summary>
    public abstract string Name { get; }

    /// <summary>The .NET type the column type reads and writes.</summary>
    public abstract Type ValueType { get; }

    /// <summary>The kinds of <see cref="ColumnSettings"/> the type takes; none for most.</summary>
    public abstract ColumnSettingKinds SupportedSettings { get; }

    /// <summary>A new column of this type: its physical type, its logical type annotation and its length. A node belongs to one schema, so make one for each.</summary>
    public abstract PrimitiveNode CreateNode(string name, FieldRepetitionType repetition);

    /// <summary>Why a column of a file cannot be read or written as this type, or null when it can.</summary>
    public abstract string? GetProblem(PrimitiveNode column);

    /// <summary>Checks that the column fits the type.</summary>
    /// <exception cref="ArgumentException">The column cannot be read or written as this type.</exception>
    public void Require(PrimitiveNode column)
    {
        if (GetProblem(column) is { } problem)
        {
            string path = column.Path.Length > 0 ? string.Join('.', column.Path) : column.Name;
            throw new ArgumentException($"Column '{path}' cannot be used as {Name}: {problem}.");
        }
    }
}
