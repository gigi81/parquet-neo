namespace ParquetNeo.Serialization;

/// <summary>A column a row type is read from: the property it goes to, and what the file's column must be like.</summary>
/// <param name="Name">The name of the column in the file.</param>
/// <param name="ValueType">The .NET type of the values, without <see cref="Nullable{T}"/>.</param>
/// <param name="Optional">Whether the property can hold null, so that the column may be missing or hold nulls.</param>
/// <param name="Description">Names the property, for messages.</param>
internal sealed record ReadColumn(string Name, Type ValueType, bool Optional, string Description);
