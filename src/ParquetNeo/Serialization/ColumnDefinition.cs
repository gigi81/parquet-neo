using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>What the serializer knows about the column a property of <typeparamref name="TRow"/> becomes.</summary>
internal abstract class ColumnDefinition<TRow>
{
    protected ColumnDefinition(string name, ParquetEncoding? encoding)
    {
        Name = name;
        Encoding = encoding;
    }

    public string Name { get; }

    /// <summary>The encoding the property asks for, or null for the default.</summary>
    public ParquetEncoding? Encoding { get; }

    /// <summary>A new schema node: a node belongs to one schema, so every file gets its own.</summary>
    public abstract PrimitiveNode CreateNode();

    public abstract IColumnBuffer<TRow> CreateBuffer(int rowGroupSize);
}
