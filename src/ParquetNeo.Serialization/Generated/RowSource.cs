using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Where the code written by the source generator takes the values of an object from: a property that is a column of its own is
/// read from the column with its number, counted from the first column of the object, and a property that is an object, a
/// collection or a dictionary from its place among the properties, counted from zero. Not for use in code you write yourself.
/// </summary>
public readonly struct RowSource
{
    private readonly ReadContext _context;
    private readonly ReadFrame _frame;

    internal RowSource(ReadContext context, ReadFrame frame)
    {
        _context = context;
        _frame = frame;
    }

    /// <summary>Takes the value of a column that cannot be null.</summary>
    /// <exception cref="ValueConversionException">The file has a null for it.</exception>
    public TValue Take<TValue>(int column) =>
        Unsafe.As<LeafColumn<TValue>>(_context.Columns[_frame.FirstColumn + column]).TryTake(out var value)
            ? value
            : throw Null(_frame.Descriptions[column]);

    /// <summary>Takes the value of a column that can be null: false when the file has a null for it.</summary>
    public bool TryTake<TValue>(int column, out TValue value) =>
        Unsafe.As<LeafColumn<TValue>>(_context.Columns[_frame.FirstColumn + column]).TryTake(out value!);

    /// <summary>Reads a property that is an object, a collection or a dictionary, and cannot be null.</summary>
    /// <exception cref="ValueConversionException">The file has a null for it.</exception>
    public TValue ReadChild<TValue>(int property)
    {
        var child = Unsafe.As<Assembler<TValue>>(_frame.Children[property]);
        return child.Read(_context, out var value) ? value : throw Null(child.Description);
    }

    /// <summary>Reads a property that is an object, a collection or a dictionary, and can be null: false when the file has a null for it.</summary>
    public bool TryReadChild<TValue>(int property, out TValue value) =>
        Unsafe.As<Assembler<TValue>>(_frame.Children[property]).Read(_context, out value!);

    private ValueConversionException Null(string? description) => new($"{description} cannot be null, but row {_context.Row} of the file has a null for it.");
}
