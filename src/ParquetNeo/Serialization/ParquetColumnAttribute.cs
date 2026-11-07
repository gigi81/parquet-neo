namespace ParquetNeo.Serialization;

/// <summary>Names the column a property is written to, or its position among the columns. Without it the column has the property's name.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetColumnAttribute : Attribute
{
    public ParquetColumnAttribute()
    {
    }

    /// <param name="name">The name of the column.</param>
    public ParquetColumnAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>The name of the column, or null for the name of the property.</summary>
    public string? Name { get; }

    /// <summary>
    /// Where the column goes among the others: lower numbers first, and properties with the same order in the order they are
    /// declared (properties of a base class before those of a derived one). Defaults to 0.
    /// </summary>
    public int Order { get; set; }
}
