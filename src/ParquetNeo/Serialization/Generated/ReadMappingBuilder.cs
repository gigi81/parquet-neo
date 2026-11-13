using ParquetNeo.ColumnTypes;
using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Describes the columns a row type is read from, and then takes the code that creates a row from their values. What the source
/// generator writes for a type uses it, and nothing at run time looks at the type: no reflection, and no code is compiled. Not for use in
/// code you write yourself.
/// </summary>
public sealed class ReadMappingBuilder<TRow>
{
    private readonly List<IPropertyAssembler> _properties = [];
    private readonly List<string> _descriptions = [];

    /// <summary>Adds the next column, in the order of the numbers the code passed to <see cref="RowSource"/> uses.</summary>
    /// <param name="name">The name of the column in the file.</param>
    /// <param name="description">What the column is, for messages: "Property 'Order.Id'".</param>
    public ReadMappingBuilder<TRow> Column<TValue>(string name, string description, bool optional)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        int slot = _properties.Count;
        _properties.Add(new PropertyAssembler<TValue>(name, description, optional, slot, new LeafAssembler<TValue>(slot, repetition: 0, description), property: null));
        _descriptions.Add(description);
        return this;
    }

    /// <param name="assemble">Creates a row from the values of the columns.</param>
    /// <exception cref="InvalidOperationException">No column was added.</exception>
    public ReadMapping<TRow> Build(AssembleRow<TRow> assemble)
    {
        ArgumentNullException.ThrowIfNull(assemble);
        if (_properties.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(TRow).Name} has no columns to read.");
        }

        var descriptions = _descriptions.ToArray();
        var root = new StructAssembler<TRow>(
            node: 0,
            objectIndex: -1,
            optional: false,
            firstColumn: 0,
            _properties.Count,
            $"Row type '{typeof(TRow).Name}'",
            _properties.ToArray(),
            constructor: null,
            context => assemble(new RowSource(context, descriptions)));
        return new ReadMapping<TRow>(new ReadModel<TRow>(root, _properties.Count, nodeCount: 1));
    }
}
