using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>What the code that shreds an object needs to know of where the object is: its columns start at <paramref name="firstColumn"/>, and its properties are shredded by <paramref name="children"/>.</summary>
/// <param name="descriptions">For messages: what the column that is this far from the first one is, where one of the object's own properties is.</param>
/// <param name="firstColumn">The position of the first column of the object among the columns that are read or written.</param>
/// <param name="children">The readers or writers of the properties of the object, in the order of its columns.</param>
internal sealed class ShredFrame(int firstColumn, string?[] descriptions, Shredder[] children)
{
    public int FirstColumn { get; } = firstColumn;

    public string?[] Descriptions { get; } = descriptions;

    public Shredder[] Children { get; } = children;
}
