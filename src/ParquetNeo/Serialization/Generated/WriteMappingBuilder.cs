using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Describes the columns a row type is written as, and then takes the code that gives the values of a row to them. What the source
/// generator writes for a type uses it, and nothing at run time looks at the type: no reflection, and no code is compiled. Not for use in
/// code you write yourself.
/// </summary>
public sealed class WriteMappingBuilder<TRow>
{
    private readonly List<PropertyShredder> _properties = [];
    private readonly List<LeafInfo> _columns = [];
    private readonly List<string> _descriptions = [];

    /// <summary>Adds the next column, in the order of the numbers the code passed to <see cref="RowSink"/> uses.</summary>
    /// <param name="name">The name of the column in the file.</param>
    /// <param name="description">What the column is, for messages: "Property 'Order.Id'".</param>
    public WriteMappingBuilder<TRow> Column<TValue>(string name, string description, bool optional, ColumnSettings? settings = null, ParquetEncoding? encoding = null, DictionarySettings? dictionary = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        var type = ColumnTypeRegistry.Get<TValue>().Configure(settings ?? new ColumnSettings());
        _properties.Add(new PropertyShredder<TValue>(name, new LeafShredder<TValue>(_columns.Count, optional, type, description), property: null, nullable: false));
        _columns.Add(new LeafInfo(name, encoding, dictionary, capacity => new LeafBuffer<TValue>(optional, hasRepetitionLevels: false, type, capacity)));
        _descriptions.Add(description);
        return this;
    }

    /// <param name="shred">Gives the values of a row to the columns.</param>
    /// <exception cref="InvalidOperationException">No column was added.</exception>
    public WriteMapping<TRow> Build(ShredRow<TRow> shred)
    {
        ArgumentNullException.ThrowIfNull(shred);
        if (_columns.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(TRow).Name} has no columns to write.");
        }

        var descriptions = _descriptions.ToArray();
        var root = new StructShredder<TRow>(
            optional: false,
            firstColumn: 0,
            _columns.Count,
            $"Row type '{typeof(TRow).Name}'",
            _properties.ToArray(),
            (row, definition, repetition, buffers) => shred(row, new RowSink(buffers, descriptions, definition, repetition)));
        return new WriteMapping<TRow>(new RowModel<TRow>(root, _columns.ToArray()));
    }
}
