using System.Linq;

namespace ParquetNeo.Generator
{
    /// <summary>An object type, with the properties that are columns.</summary>
    /// <param name="Type">The type as C# for the generated code.</param>
    /// <param name="Properties">The properties written as columns, in the order of the columns; those that can be set, or are taken by the constructor, are read.</param>
    /// <param name="ReadProblem">What stops rows of it from being created by generated code, or null; it includes what stops the objects inside it.</param>
    internal sealed record ObjectModel(string Type, EquatableArray<PropertyModel> Properties, string? ReadProblem)
    {
        /// <summary>The number of columns the object is written as.</summary>
        public int WriteColumns => Properties.Sum(p => p.Shape.WriteColumns);

        /// <summary>The number of columns the object is read from: those of the properties that can be set.</summary>
        public int ReadColumns => Properties.Where(p => p.Read).Sum(p => p.Shape.ReadColumns);

        /// <summary>Whether the object is created by a constructor with parameters, which take the values of some of the properties.</summary>
        public bool UsesConstructor => Properties.Any(p => p.Parameter >= 0);
    }
}
