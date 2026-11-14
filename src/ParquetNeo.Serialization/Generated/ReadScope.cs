using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Generated;

/// <summary>An object, collection or dictionary that a <see cref="ReadMappingBuilder{TRow}"/> has begun and not yet ended.</summary>
/// <param name="name">What the part is called among the properties of the object it is in; null for an element, a key or a value.</param>
/// <param name="repetition">How many collections the part is in.</param>
/// <param name="node">The number of the part among the objects, collections and dictionaries.</param>
/// <param name="firstColumn">The number of the first column below the part.</param>
internal abstract class ReadScope(string? name, string description, bool optional, int repetition, int node, int firstColumn)
{
    public string? Name { get; } = name;

    public string Description { get; } = description;

    public bool Optional { get; } = optional;

    public int Repetition { get; } = repetition;

    public int Node { get; } = node;

    public int FirstColumn { get; } = firstColumn;

    /// <summary>How many collections what is inside the part is in.</summary>
    public abstract int ChildRepetition { get; }

    /// <summary>What the next part inside is, for messages; <paramref name="given"/> is what the generated code says of a property.</summary>
    public abstract string ChildDescription(string? given);

    /// <summary>Takes the next part inside, which is described.</summary>
    public abstract void Accept(string? childName, string childDescription, bool childOptional, Assembler child);

    /// <summary>Makes the assembler of the part, when everything inside it was described.</summary>
    public abstract Assembler Finish(int columnCount);
}
