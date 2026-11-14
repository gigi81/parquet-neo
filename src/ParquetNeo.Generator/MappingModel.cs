namespace ParquetNeo.Generator
{
    /// <summary>What the generator knows about a row type and the object types inside it, enough to write its mapping.</summary>
    /// <param name="Type">The row type as C# for the generated code.</param>
    /// <param name="Objects">The row type and every object type inside it, each once.</param>
    internal sealed record MappingModel(string Type, EquatableArray<ObjectModel> Objects);
}
