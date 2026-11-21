using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>An object: a group of the properties it is written as, optional when the object may be null.</summary>
/// <param name="properties">The properties, which say how the object is laid out in the schema.</param>
/// <param name="shredProperties">One function that reads all the properties and adds them to their columns, compiled for the type or written for it by the source generator.</param>
/// <param name="optional">Whether the value can be null: the field is optional.</param>
/// <param name="firstColumn">The position of the first column of the object among the columns that are read or written.</param>
/// <param name="columnCount">How many columns the object has.</param>
/// <param name="description">Describes the column or property, for the messages of errors.</param>
internal sealed class StructShredder<TValue>(
    bool optional,
    int firstColumn,
    int columnCount,
    string description,
    PropertyShredder[] properties,
    Action<TValue, int, int, ILeafBuffer[]> shredProperties)
    : Shredder<TValue>(optional, firstColumn, columnCount, description)
{
    public override void Shred(TValue value, bool present, int definition, int repetition, ILeafBuffer[] buffers)
    {
        if (!present)
        {
            if (!Optional)
            {
                throw Missing();
            }

            AddMissing(buffers, definition, repetition);
            return;
        }

        ShredProperties(value, Optional ? definition + 1 : definition, repetition, buffers);
    }

    /// <summary>Shreds the properties of an object that is there; the root, which is not a group of the file, starts here.</summary>
    public void ShredProperties(TValue value, int definition, int repetition, ILeafBuffer[] buffers) =>
        shredProperties(value, definition, repetition, buffers);

    public override SchemaNode CreateSchemaNode(string name) => new GroupNode(name, Repetition, CreateChildren());

    public IEnumerable<SchemaNode> CreateChildren() => properties.Select(p => p.CreateSchemaNode());
}
