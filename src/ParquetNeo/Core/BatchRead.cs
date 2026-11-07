namespace ParquetNeo.Core;

/// <summary>The outcome of a <see cref="ColumnReader"/> batch read.</summary>
/// <param name="Rows">The number of rows read; 0 means the column chunk has no more rows.</param>
/// <param name="Values">The number of non-null values among them (equal to <paramref name="Rows"/> for a required column).</param>
public readonly record struct BatchRead(int Rows, int Values);
