using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>An object, collection or dictionary that a <see cref="WriteMappingBuilder{TRow}"/> has begun and not yet ended: the parts of it that were described so far.</summary>
/// <param name="name">What the part is called among the properties of the object it is in; null for an element, a key or a value.</param>
/// <param name="outside">Where the part is in the schema: how many optional and repeated nodes lie above it.</param>
/// <param name="firstColumn">The number of the first column below the part.</param>
internal abstract class WriteScope(string? name, string path, string description, bool optional, Position outside, int firstColumn)
{
    public string? Name { get; } = name;

    public string Path { get; } = path;

    public string Description { get; } = description;

    public bool Optional { get; } = optional;

    public Position Outside { get; } = outside;

    public int FirstColumn { get; } = firstColumn;

    /// <summary>Where what is inside the part is in the schema.</summary>
    public abstract Position ChildPosition { get; }

    /// <summary>The path of the next part inside, as it is named in the file.</summary>
    public abstract string ChildPath(string? childName);

    /// <summary>What the next part inside is, for messages; <paramref name="given"/> is what the generated code says of a property.</summary>
    public abstract string ChildDescription(string? given);

    /// <summary>Takes the next part inside, which is described.</summary>
    public abstract void Accept(string? childName, Shredder child);

    /// <summary>Makes the shredder of the part, when everything inside it was described.</summary>
    public abstract Shredder Finish(int columnCount);
}
