namespace ParquetNeo.Serialization;

/// <summary>A value stored as it is.</summary>
internal sealed class PlainLeafShredder<TValue>(int column, bool optional, ColumnShape shape, string description)
    : LeafShredder<TValue, TValue>(column, optional, shape, description)
{
    protected override TValue Store(TValue value) => value;
}
