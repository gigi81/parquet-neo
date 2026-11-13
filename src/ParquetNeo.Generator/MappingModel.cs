namespace ParquetNeo.Generator
{
    /// <summary>What the generator knows about a row type, enough to write its mapping.</summary>
    /// <param name="TypeName">The type as C# for the generated code.</param>
    /// <param name="WriteColumns">The properties written as columns, in the order of the columns.</param>
    /// <param name="ReadColumns">The properties read from columns, which are those with a setter; empty when rows cannot be created by the generated code.</param>
    internal sealed record MappingModel(
        string TypeName,
        EquatableArray<ColumnModel> WriteColumns,
        EquatableArray<ColumnModel> ReadColumns);
}
