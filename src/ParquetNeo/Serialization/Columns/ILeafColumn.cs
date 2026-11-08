using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Columns;

/// <summary>
/// The entries of one column of a row group, read into memory so that rows can be assembled from several columns at once, one
/// entry after the other. A column the file does not have stands in with entries that are all missing, however many are taken.
/// </summary>
internal interface ILeafColumn
{
    /// <summary>Reads all the entries of the column chunk, or turns into a column of missing entries when <paramref name="reader"/> is null.</summary>
    void Fill(ColumnReader? reader);

    /// <summary>The definition level of the next entry.</summary>
    int Definition { get; }

    /// <summary>The repetition level of the next entry.</summary>
    int Repetition { get; }

    /// <summary>Whether every entry has been taken. Always true for a column the file does not have.</summary>
    bool AtEnd { get; }

    /// <summary>Takes the next entry without looking at its value, for a part of the row that is missing.</summary>
    void Skip();

    /// <summary>The path of the column, for messages.</summary>
    string Name { get; }
}
