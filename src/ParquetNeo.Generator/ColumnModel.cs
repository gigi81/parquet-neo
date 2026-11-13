namespace ParquetNeo.Generator
{
    /// <summary>A property of a row type that is a column of the file.</summary>
    /// <param name="Name">The name of the column.</param>
    /// <param name="PropertyName">The name of the property, which the generated code reads and sets.</param>
    /// <param name="Description">What it is, for messages the library makes: Property 'Order.Id'.</param>
    /// <param name="ValueType">The type of a value of the column, as C# for the generated code: without the question mark of a nullable type.</param>
    /// <param name="Optional">Whether the column can have nulls.</param>
    /// <param name="IsEnum">Whether the type is an enum, which the library must be told of when it cannot make types at run time.</param>
    /// <param name="Settings">The code that makes the settings of the column type from its attributes, or null.</param>
    /// <param name="Encoding">The code that makes the encoding asked for, or null.</param>
    /// <param name="Dictionary">The code that makes the dictionary settings asked for, or null.</param>
    internal sealed record ColumnModel(
        string Name,
        string PropertyName,
        string Description,
        string ValueType,
        bool Optional,
        bool IsEnum,
        string? Settings,
        string? Encoding,
        string? Dictionary);
}
