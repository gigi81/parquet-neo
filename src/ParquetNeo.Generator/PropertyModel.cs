namespace ParquetNeo.Generator
{
    /// <summary>A property of an object that is written as columns.</summary>
    /// <param name="Name">The name of the column or group in the file.</param>
    /// <param name="PropertyName">The name of the property, which the generated code reads and sets.</param>
    /// <param name="Description">What it is, for messages the library makes: Property 'Order.Id'.</param>
    /// <param name="CanSet">Whether it has a public setter, so that it is read too.</param>
    internal sealed record PropertyModel(string Name, string PropertyName, string Description, bool CanSet, ShapeModel Shape);
}
