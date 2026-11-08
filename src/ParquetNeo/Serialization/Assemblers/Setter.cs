namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Sets a property of a row that is being built; the row is passed by reference so that structs can be set too.</summary>
internal delegate void Setter<TRow, in TValue>(ref TRow row, TValue value);
