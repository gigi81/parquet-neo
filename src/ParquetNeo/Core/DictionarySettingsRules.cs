using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>Which <see cref="DictionarySettings"/> <see cref="ParquetWriterOptions.ColumnDictionaries"/> picked for a column.</summary>
internal static class DictionarySettingsRules
{
    private static readonly DictionarySettings Default = new();

    /// <summary>Checks every entry of <paramref name="columnDictionaries"/> against the schema.</summary>
    /// <exception cref="ArgumentException">An entry names a column that does not exist, a mode that is not defined or a size limit below 1.</exception>
    public static void Validate(ParquetSchema schema, IReadOnlyDictionary<string, DictionarySettings>? columnDictionaries)
    {
        if (columnDictionaries is null)
        {
            return;
        }

        foreach (var (path, settings) in columnDictionaries)
        {
            if (!schema.Columns.Any(c => string.Join('.', c.Path) == path))
            {
                throw new ArgumentException($"ColumnDictionaries names '{path}', which is not a column of the schema.");
            }

            if (settings is null)
            {
                throw new ArgumentException($"The dictionary settings of column '{path}' are null.");
            }

            if (!Enum.IsDefined(settings.Mode))
            {
                throw new ArgumentException($"The dictionary mode {(int)settings.Mode} of column '{path}' is not defined.");
            }

            if (settings.SizeLimit is < 1)
            {
                throw new ArgumentException($"The dictionary size limit of column '{path}' must be at least 1, not {settings.SizeLimit}.");
            }
        }
    }

    public static DictionarySettings For(PrimitiveNode column, IReadOnlyDictionary<string, DictionarySettings>? columnDictionaries) =>
        columnDictionaries is not null && columnDictionaries.TryGetValue(string.Join('.', column.Path), out var settings) ? settings : Default;
}
