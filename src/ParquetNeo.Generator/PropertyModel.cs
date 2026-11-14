namespace ParquetNeo.Generator
{
    /// <summary>A property of an object that is written as columns.</summary>
    /// <param name="Name">The name of the column or group in the file.</param>
    /// <param name="PropertyName">The name of the property, which the generated code reads and sets.</param>
    /// <param name="Description">What it is, for messages the library makes: Property 'Order.Id'.</param>
    /// <param name="CanSet">Whether it has a public setter, so that it is read too.</param>
    /// <param name="Parameter">The place of the parameter of the constructor the object is created with that takes its value, or -1 when it is none.</param>
    internal sealed record PropertyModel(string Name, string PropertyName, string Description, bool CanSet, int Parameter, ShapeModel Shape)
    {
        /// <summary>Whether it is read: it is a parameter of the constructor, or it can be set.</summary>
        public bool Read => CanSet || Parameter >= 0;
    }
}
