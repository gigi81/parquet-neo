namespace ParquetNeo.Serialization;

/// <summary>Reads a property of a row; returns false when the property holds no value (a null).</summary>
internal delegate bool TryGetter<in TRow, TValue>(TRow row, out TValue value);
