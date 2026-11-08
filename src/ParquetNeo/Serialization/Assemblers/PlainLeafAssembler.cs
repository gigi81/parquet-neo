namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A value read as it is stored.</summary>
internal sealed class PlainLeafAssembler<TValue>(int slot) : LeafAssembler<TValue, TValue>(slot)
{
    protected override TValue Convert(TValue stored) => stored;
}
