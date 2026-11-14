using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Where the code written by the source generator puts the values of an object: a property that is a column of its own goes to the
/// column with its number, counted from the first column of the object, and a property that is an object, a collection or a dictionary
/// goes to its place among the properties, counted from zero. Not for use in code you write yourself.
/// </summary>
public readonly struct RowSink
{
    private readonly ShredFrame _frame;
    private readonly ILeafBuffer[] _buffers;
    private readonly int _definition;
    private readonly int _repetition;

    internal RowSink(ShredFrame frame, ILeafBuffer[] buffers, int definition, int repetition)
    {
        _frame = frame;
        _buffers = buffers;
        _definition = definition;
        _repetition = repetition;
    }

    /// <summary>Adds the value of a column that cannot be null.</summary>
    public void Required<TValue>(int column, TValue value)
    {
        if (!typeof(TValue).IsValueType && value is null)
        {
            throw new MissingRequiredValueException($"{_frame.Descriptions[column]} is null, but its column is required. Make it nullable to store nulls.");
        }

        Unsafe.As<LeafBuffer<TValue>>(_buffers[_frame.FirstColumn + column]).AddValue(value, _definition, _repetition);
    }

    /// <summary>Adds the value of a column that can be null, which is there.</summary>
    public void Present<TValue>(int column, TValue value) =>
        Unsafe.As<LeafBuffer<TValue>>(_buffers[_frame.FirstColumn + column]).AddValue(value, _definition + 1, _repetition);

    /// <summary>Adds a null to a column that can be null.</summary>
    public void Missing(int column) => _buffers[_frame.FirstColumn + column].AddMissing(_definition, _repetition);

    /// <summary>Hands the value of a property that is an object, a collection or a dictionary to what was described for it.</summary>
    /// <param name="property">The place of the property among those of the object.</param>
    /// <param name="present">False for a null, which is refused when the property cannot be null.</param>
    public void Child<TValue>(int property, TValue value, bool present) =>
        Unsafe.As<Shredder<TValue>>(_frame.Children[property]).Shred(value, present, _definition, _repetition, _buffers);
}
