namespace ParquetNeo.DataModel;

/// <summary>Leaves a property out of the file.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetIgnoreAttribute : Attribute
{
}
