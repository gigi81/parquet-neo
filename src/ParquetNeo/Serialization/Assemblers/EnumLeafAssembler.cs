namespace ParquetNeo.Serialization.Assemblers;

/// <summary>An enum, read from the name of its value.</summary>
internal sealed class EnumLeafAssembler<TEnum>(int slot, int repetition, string description) : LeafAssembler<TEnum, string>(slot, repetition, description)
    where TEnum : struct, Enum
{
    protected override TEnum Convert(string stored) => EnumParser<TEnum>.Parse(stored);
}
