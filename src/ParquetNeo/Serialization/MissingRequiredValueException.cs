namespace ParquetNeo.Serialization;

/// <summary>A null where the schema says there is always a value; the serializer turns it into an <see cref="ArgumentException"/> that names the row.</summary>
internal sealed class MissingRequiredValueException(string description) : Exception(description)
{
}
