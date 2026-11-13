using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Where the code written by the source generator puts the values of a row: each goes to the column with its number, in the order
/// the columns were added to the <see cref="WriteMappingBuilder{TRow}"/>. Not for use in code you write yourself.
/// </summary>
public readonly struct RowSink
{
    private readonly ILeafBuffer[] _buffers;
    private readonly string[] _descriptions;
    private readonly int _definition;
    private readonly int _repetition;

    internal RowSink(ILeafBuffer[] buffers, string[] descriptions, int definition, int repetition)
    {
        _buffers = buffers;
        _descriptions = descriptions;
        _definition = definition;
        _repetition = repetition;
    }

    /// <summary>Adds the value of a column that cannot be null.</summary>
    public void Required<TValue>(int column, TValue value)
    {
        if (!typeof(TValue).IsValueType && value is null)
        {
            throw new MissingRequiredValueException($"{_descriptions[column]} is null, but its column is required. Make it nullable to store nulls.");
        }

        Unsafe.As<LeafBuffer<TValue>>(_buffers[column]).AddValue(value, _definition, _repetition);
    }

    /// <summary>Adds the value of a column that can be null, which is there.</summary>
    public void Present<TValue>(int column, TValue value) =>
        Unsafe.As<LeafBuffer<TValue>>(_buffers[column]).AddValue(value, _definition + 1, _repetition);

    /// <summary>Adds a null to a column that can be null.</summary>
    public void Missing(int column) => _buffers[column].AddMissing(_definition, _repetition);
}
