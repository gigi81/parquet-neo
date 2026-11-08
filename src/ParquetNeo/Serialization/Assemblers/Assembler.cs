using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Builds a part of a row from the entries of the columns below it: the reverse of shredding.</summary>
internal abstract class Assembler
{
    /// <summary>Adds an empty column for each column of the part, in order; the position of a column is its slot.</summary>
    public abstract void AddColumns(List<ILeafColumn> columns);
}
