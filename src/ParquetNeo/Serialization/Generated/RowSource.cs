using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Where the code written by the source generator takes the values of a row from: the next value of the column with a number, in
/// the order the columns were added to the <see cref="ReadMappingBuilder{TRow}"/>. Not for use in code you write yourself.
/// </summary>
public readonly struct RowSource
{
    private readonly ReadContext _context;
    private readonly string[] _descriptions;

    internal RowSource(ReadContext context, string[] descriptions)
    {
        _context = context;
        _descriptions = descriptions;
    }

    /// <summary>Takes the value of a column that cannot be null.</summary>
    /// <exception cref="ValueConversionException">The file has a null for it.</exception>
    public TValue Take<TValue>(int column) =>
        Unsafe.As<LeafColumn<TValue>>(_context.Columns[column]).TryTake(out var value)
            ? value
            : throw new ValueConversionException($"{_descriptions[column]} cannot be null, but row {_context.Row} of the file has a null for it.");

    /// <summary>Takes the value of a column that can be null: false when the file has a null for it.</summary>
    public bool TryTake<TValue>(int column, out TValue value) =>
        Unsafe.As<LeafColumn<TValue>>(_context.Columns[column]).TryTake(out value!);
}
