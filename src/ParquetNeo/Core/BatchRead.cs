namespace ParquetNeo.Core;

/// <summary>The outcome of a <see cref="ColumnReader"/> batch read.</summary>
/// <param name="Entries">
/// The number of level entries read; 0 means the column chunk has no more. An entry is a row for a column that is not
/// repeated; in a repeated column it is one value, null or empty list, and a row can span several entries.
/// </param>
/// <param name="Values">The number of non-null values among them (equal to <paramref name="Entries"/> for a required column).</param>
public readonly record struct BatchRead(int Entries, int Values);
