namespace ParquetNeo.Core;

/// <summary>How a column is dictionary encoded; see <see cref="ParquetWriterOptions.ColumnDictionaries"/>.</summary>
public enum DictionaryMode
{
    /// <summary>
    /// Every distinct value goes into a dictionary until the dictionary passes <see cref="ParquetWriterOptions.DictionaryPageSizeLimit"/>,
    /// and then the rest of the column is written plain. The best choice for data that repeats; it is the default.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// As <see cref="Standard"/>, but the writer looks at the first values of the column (the first 32 KiB of them) and, when
    /// almost all of them (more than nine in ten) are different, such as identifiers, drops the dictionary and writes the rest of
    /// the column plain. That saves filling a dictionary that would be dropped anyway, which is much of the time of such a column,
    /// and the larger file. A column that repeats only after its first values has no dictionary.
    /// </summary>
    Adaptive = 1,

    /// <summary>The column is written plain, whatever its values. The choice for a column that is known to be unique.</summary>
    Disabled = 2,
}
