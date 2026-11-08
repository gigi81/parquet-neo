namespace ParquetNeo.Serialization.Assemblers;

/// <summary>An enum, read from the name of its value.</summary>
internal sealed class EnumLeafAssembler<TEnum>(int slot) : LeafAssembler<TEnum, string>(slot)
    where TEnum : struct, Enum
{
    protected override TEnum Convert(string stored) => EnumParser<TEnum>.Parse(stored);
}
