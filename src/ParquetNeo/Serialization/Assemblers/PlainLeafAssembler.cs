namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A value read as it is stored.</summary>
internal sealed class PlainLeafAssembler<TValue>(int slot, int repetition, string description) : LeafAssembler<TValue, TValue>(slot, repetition, description)
{
    protected override TValue Convert(TValue stored) => stored;
}
