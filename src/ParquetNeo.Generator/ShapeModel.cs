namespace ParquetNeo.Generator
{
    /// <summary>How a value of a property (or of an element, a key or a value) is stored: one column, or the columns of an object, a collection or a dictionary.</summary>
    /// <param name="Type">
    /// The type as C# for the generated code, without the question mark of a nullable type: for a column of its own the type of its
    /// values, for an object, collection or dictionary the type itself.
    /// </param>
    /// <param name="Optional">Whether it can be null.</param>
    /// <param name="IsValueType">Whether <paramref name="Type"/> is a struct.</param>
    /// <param name="IsNullableValue">Whether it is a <c>Nullable&lt;T&gt;</c> of <paramref name="Type"/>, which is then a struct.</param>
    /// <param name="WriteColumns">The number of columns it is written as.</param>
    /// <param name="ReadColumns">The number of columns it is read from, which is less when some properties of an object it holds cannot be set.</param>
    /// <param name="IsEnum">For a column of its own: whether it is an enum, which the library must be told of when it cannot make types at run time.</param>
    /// <param name="Settings">For a column of its own: the code that makes the settings of its column type from the attributes, or null.</param>
    /// <param name="Encoding">For a column of its own: the code that makes the encoding asked for, or null.</param>
    /// <param name="Dictionary">For a column of its own: the code that makes the dictionary settings asked for, or null.</param>
    /// <param name="Element">For a collection its element, for a dictionary its value.</param>
    /// <param name="Key">For a dictionary its key.</param>
    /// <param name="Create">For a collection that is created first and then added to, and for a dictionary: the code of a function that creates it. Null for one made from what was read.</param>
    /// <param name="ReadProblem">What stops it from being read, or null.</param>
    internal sealed record ShapeModel(
        ShapeKind Kind,
        string Type,
        bool Optional,
        bool IsValueType,
        bool IsNullableValue,
        int WriteColumns,
        int ReadColumns,
        bool IsEnum,
        string? Settings,
        string? Encoding,
        string? Dictionary,
        ShapeModel? Element,
        ShapeModel? Key,
        string? Create,
        string? ReadProblem);
}
