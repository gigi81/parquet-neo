using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Everything that changes while rows are read from one file: the columns of the row group, the objects under construction and the row.</summary>
internal sealed class ReadContext(ILeafColumn[] columns, object[] objects, NodeBinding[] nodes)
{
    public ILeafColumn[] Columns { get; } = columns;

    /// <summary>One <see cref="RowContext{TRow}"/> for each type of object that is built.</summary>
    public object[] Objects { get; } = objects;

    public NodeBinding[] Nodes { get; } = nodes;

    /// <summary>The number of the row being built in the file, for messages.</summary>
    public long Row { get; set; }
}
