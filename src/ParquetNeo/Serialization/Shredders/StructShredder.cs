using System.Linq.Expressions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>An object: a group of the properties it is written as, optional when the object may be null.</summary>
internal sealed class StructShredder<TValue>(bool optional, int firstColumn, int columnCount, string description, PropertyShredder[] properties)
    : Shredder<TValue>(optional, firstColumn, columnCount, description)
{
    /// <summary>
    /// The shredding of all the properties in one function generated for the type, instead of a loop over small objects that each
    /// read a property through a delegate and call the shredder of its type: the properties are read and added to their columns
    /// (or handed to the shredder of an object, collection or dictionary) in one piece of code.
    /// </summary>
    private readonly Action<TValue, int, int, ILeafBuffer[]> _shredProperties = Compile(properties);

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
        _shredProperties(value, definition, repetition, buffers);

    public override SchemaNode CreateSchemaNode(string name) => new GroupNode(name, Repetition, CreateChildren());

    public IEnumerable<SchemaNode> CreateChildren() => properties.Select(p => p.CreateSchemaNode());

    private static Action<TValue, int, int, ILeafBuffer[]> Compile(PropertyShredder[] properties)
    {
        var parent = Expression.Parameter(typeof(TValue), "row");
        var definition = Expression.Parameter(typeof(int), "definition");
        var repetition = Expression.Parameter(typeof(int), "repetition");
        var buffers = Expression.Parameter(typeof(ILeafBuffer[]), "buffers");
        var steps = properties.Select(property => property.Emit(parent, definition, repetition, buffers)).ToList();
        return Expression.Lambda<Action<TValue, int, int, ILeafBuffer[]>>(Expression.Block(steps), parent, definition, repetition, buffers).Compile();
    }
}
