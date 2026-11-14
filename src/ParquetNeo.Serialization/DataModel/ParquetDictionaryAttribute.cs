
namespace ParquetNeo.DataModel;

/// <summary>
/// How the column of a property is dictionary encoded (every column of a collection or dictionary, but not of the properties of an
/// object inside it, which have their own attributes), instead of <see cref="DictionaryMode.Standard"/>. A dictionary stores each
/// distinct value once and the rows refer to it, which pays off when values repeat and is wasted work when they do not.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="DictionaryMode.Adaptive"/> on a property that is sometimes unique and sometimes repeated, depending on the data
/// written, so that the writer drops the dictionary when the first values show it is of no use. Typical properties:
/// a reference or order number (<c>"ORD-000123"</c>), a <see cref="Guid"/> or a name that is nearly unique, a timestamp that is
/// recorded to the millisecond, or a measurement such as a price, a temperature or a sensor reading.
/// </para>
/// <para>
/// Use <see cref="DictionaryMode.Disabled"/> on a property that is known to be unique, such as a primary key or an
/// auto-incremented identifier, or whose values are never repeated by nature, such as a free text comment or a url. Nothing is lost,
/// since a dictionary of unique values is larger than the values themselves.
/// </para>
/// <para>
/// Leave it out (<see cref="DictionaryMode.Standard"/>) for properties that take few distinct values: a country, a status, a
/// currency, a category, a boolean-like flag stored as text, a date without a time, a small quantity.
/// </para>
/// <para>
/// <see cref="SizeLimit"/> raises (or lowers) how big the dictionary of the column may grow before the writer gives it up, for
/// a column with few distinct values that are large, such as long descriptions or urls that come again and again.
/// </para>
/// <para>What is set in the options (<see cref="ParquetWriterOptions.ColumnDictionaries"/>) wins over this attribute.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetDictionaryAttribute(DictionaryMode mode) : Attribute
{
    public DictionaryMode Mode { get; } = mode;

    /// <summary>
    /// The size, in bytes, the dictionary may reach before the rest of the column is written plain, in place of
    /// <see cref="ParquetWriterOptions.DictionaryPageSizeLimit"/>. Zero, the default, leaves the limit of the writer. The
    /// dictionary and the pages that use it are held in memory until the column is complete, so a big limit costs memory.
    /// </summary>
    public int SizeLimit { get; init; }
}
