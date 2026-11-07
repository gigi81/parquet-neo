using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>
/// Splits the values of one part of the row (a property, an element, a key) into the entries of the columns below it:
/// the Dremel shredding of the part. The columns that belong to it are a run in the depth-first order of the schema.
/// </summary>
internal abstract class Shredder
{
    protected Shredder(bool optional, int firstColumn, int columnCount, string description)
    {
        Optional = optional;
        FirstColumn = firstColumn;
        ColumnCount = columnCount;
        Description = description;
    }

    /// <summary>Whether the value can be missing: a null, in the row, where the schema says optional.</summary>
    public bool Optional { get; }

    /// <summary>The index of the first column below this part among the columns of the file.</summary>
    public int FirstColumn { get; }

    public int ColumnCount { get; }

    /// <summary>Says where the part is, for the message when it is null but must not be.</summary>
    public string Description { get; }

    protected FieldRepetitionType Repetition => Optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required;

    /// <summary>A new schema node for the part: a node belongs to one schema, so every schema gets its own.</summary>
    public abstract SchemaNode CreateSchemaNode(string name);

    /// <summary>Gives every column below this part the entry for a value that is missing from <paramref name="definition"/> levels down.</summary>
    protected void AddMissing(ILeafBuffer[] buffers, int definition, int repetition)
    {
        for (int column = FirstColumn; column < FirstColumn + ColumnCount; column++)
        {
            buffers[column].AddMissing(definition, repetition);
        }
    }

    protected MissingRequiredValueException Missing() => new($"{Description} is null, but its column is required. Make it nullable to store nulls.");
}
