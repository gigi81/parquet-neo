namespace ParquetNeo.Serialization;

/// <summary>An enum, stored as the name of its value.</summary>
internal sealed class EnumLeafShredder<TEnum>(int column, bool optional, ColumnShape shape, string description)
    : LeafShredder<TEnum, string>(column, optional, shape, description)
    where TEnum : struct, Enum
{
    protected override string Store(TEnum value) => EnumNames<TEnum>.Of(value);
}
