using System.Linq.Expressions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>An object: a group of the properties it is written as, optional when the object may be null.</summary>
internal sealed class StructShredder<TValue>(bool optional, int firstColumn, int columnCount, string description, PropertyShredder<TValue>[] properties)
    : Shredder<TValue>(optional, firstColumn, columnCount, description)
{
    /// <summary>
    /// When every property is a single column: the shredding of all of them in one compiled function, for an object that has no
    /// optional or repeated ancestor. It does what the loop over the properties does without the calls between the small objects
    /// that make it up.
    /// </summary>
    private readonly Action<TValue, ILeafBuffer[]>? _flat = CompileFlat(properties);

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
    public void ShredProperties(TValue value, int definition, int repetition, ILeafBuffer[] buffers)
    {
        if (_flat is not null && definition == 0 && repetition == 0)
        {
            _flat(value, buffers);
            return;
        }

        foreach (var property in properties)
        {
            property.Shred(value, definition, repetition, buffers);
        }
    }

    public override SchemaNode CreateSchemaNode(string name) => new GroupNode(name, Repetition, CreateChildren());

    public IEnumerable<SchemaNode> CreateChildren() => properties.Select(p => p.CreateSchemaNode());

    private static Action<TValue, ILeafBuffer[]>? CompileFlat(PropertyShredder<TValue>[] properties)
    {
        var parent = Expression.Parameter(typeof(TValue), "row");
        var buffers = Expression.Parameter(typeof(ILeafBuffer[]), "buffers");
        var steps = new List<Expression>(properties.Length);
        foreach (var property in properties)
        {
            if (property.EmitFlat(parent, buffers) is not { } step)
            {
                return null;
            }

            steps.Add(step);
        }

        return Expression.Lambda<Action<TValue, ILeafBuffer[]>>(Expression.Block(steps), parent, buffers).Compile();
    }
}
