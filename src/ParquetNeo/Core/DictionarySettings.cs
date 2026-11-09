namespace ParquetNeo.Core;

/// <summary>How one column is dictionary encoded; see <see cref="ParquetWriterOptions.ColumnDictionaries"/>.</summary>
/// <remarks>A <see cref="DictionaryMode"/> alone can be used where settings are expected, to choose the mode and keep the size limit.</remarks>
public sealed record DictionarySettings
{
    /// <summary>Whether and when the column has a dictionary. Defaults to <see cref="DictionaryMode.Standard"/>.</summary>
    public DictionaryMode Mode { get; init; } = DictionaryMode.Standard;

    /// <summary>
    /// The size, in bytes, the dictionary of this column may reach before the writer stops adding to it and writes the rest of the
    /// column plain; see <see cref="ParquetWriterOptions.DictionaryPageSizeLimit"/>, which is used when this is null. Raise it for a
    /// column with few distinct values that are large (long texts, urls), whose dictionary would otherwise be dropped half way
    /// through. Everything the dictionary and the pages that use it take is held in memory until the column is complete.
    /// </summary>
    public int? SizeLimit { get; init; }

    /// <summary>The settings that choose only a mode.</summary>
    public static DictionarySettings FromMode(DictionaryMode mode) => new() { Mode = mode };

    public static implicit operator DictionarySettings(DictionaryMode mode) => FromMode(mode);
}
