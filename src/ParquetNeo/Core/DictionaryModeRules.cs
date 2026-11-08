using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>Which <see cref="DictionaryMode"/> <see cref="ParquetWriterOptions.ColumnDictionaries"/> picked for a column.</summary>
internal static class DictionaryModeRules
{
    /// <summary>Checks every entry of <paramref name="columnDictionaries"/> against the schema.</summary>
    /// <exception cref="ArgumentException">An entry names a column that does not exist, or a mode that is not defined.</exception>
    public static void Validate(ParquetSchema schema, IReadOnlyDictionary<string, DictionaryMode>? columnDictionaries)
    {
        if (columnDictionaries is null)
        {
            return;
        }

        foreach (var (path, mode) in columnDictionaries)
        {
            if (!schema.Columns.Any(c => string.Join('.', c.Path) == path))
            {
                throw new ArgumentException($"ColumnDictionaries names '{path}', which is not a column of the schema.");
            }

            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentException($"The dictionary mode {(int)mode} of column '{path}' is not defined.");
            }
        }
    }

    public static DictionaryMode For(PrimitiveNode column, IReadOnlyDictionary<string, DictionaryMode>? columnDictionaries) =>
        columnDictionaries is not null && columnDictionaries.TryGetValue(string.Join('.', column.Path), out var mode) ? mode : DictionaryMode.Standard;
}
