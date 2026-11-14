namespace ParquetNeo.Generator
{
    /// <summary>What a property, an element, a key or a value is stored as.</summary>
    internal enum ShapeKind
    {
        /// <summary>A column of its own.</summary>
        Leaf,

        /// <summary>A group of the columns of its properties.</summary>
        Object,

        /// <summary>A LIST of elements.</summary>
        List,

        /// <summary>A MAP of keys and values.</summary>
        Map,
    }
}
