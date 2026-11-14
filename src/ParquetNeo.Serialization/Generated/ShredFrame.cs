using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.Serialization.Generated;

/// <summary>What the code that shreds an object needs to know of where the object is: its columns start at <paramref name="firstColumn"/>, and its properties are shredded by <paramref name="children"/>.</summary>
/// <param name="descriptions">For messages: what the column that is this far from the first one is, where one of the object's own properties is.</param>
internal sealed class ShredFrame(int firstColumn, string?[] descriptions, Shredder[] children)
{
    public int FirstColumn { get; } = firstColumn;

    public string?[] Descriptions { get; } = descriptions;

    public Shredder[] Children { get; } = children;
}
