using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>A <see cref="Shredder"/> for values of a .NET type.</summary>
internal abstract class Shredder<TValue>(bool optional, int firstColumn, int columnCount, string description)
    : Shredder(optional, firstColumn, columnCount, description)
{
    /// <summary>Adds the entries of a value to the columns below.</summary>
    /// <param name="present">False for a null: <paramref name="value"/> is then meaningless.</param>
    /// <param name="definition">The number of optional and repeated ancestors that are present.</param>
    /// <param name="repetition">The repetition level of the first entry: how deep the entry shares its ancestors with the one before.</param>
    public abstract void Shred(TValue value, bool present, int definition, int repetition, ILeafBuffer[] buffers);
}
