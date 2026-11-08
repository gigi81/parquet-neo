namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Sets a property of a row that is being built to null, or the default of its type.</summary>
internal delegate void DefaultSetter<TRow>(ref TRow row);
