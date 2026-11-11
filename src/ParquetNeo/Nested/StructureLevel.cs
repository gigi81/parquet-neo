using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>
/// The structure one optional or repeated schema node adds to a column: validity for an optional node, offsets for a
/// repeated one. Create it with <see cref="ForOptional"/> or <see cref="ForRepeated"/>.
/// </summary>
public sealed class StructureLevel
{
    private StructureLevel(SchemaNode node, StructureLevelKind kind, bool[]? validity, int[]? offsets)
    {
        Node = node;
        Kind = kind;
        Validity = validity;
        Offsets = offsets;
    }

    public SchemaNode Node { get; }

    public StructureLevelKind Kind { get; }

    /// <summary>For an optional node, one entry per item: <c>true</c> when the value is present.</summary>
    public bool[]? Validity { get; }

    /// <summary>
    /// For a repeated node, one entry more than the items of the enclosing level: the elements of item <c>i</c> are the
    /// elements <c>Offsets[i]</c> up to (not including) <c>Offsets[i + 1]</c>. The first offset is 0.
    /// </summary>
    public int[]? Offsets { get; }

    /// <summary>The number of items this level has an entry for: the length of the validity, or one less than the offsets.</summary>
    public int ItemCount => Kind == StructureLevelKind.Optional ? Validity!.Length : Offsets!.Length - 1;

    /// <summary>The number of elements of a repeated node, over all its items (0 for an optional node).</summary>
    public int ElementCount => Kind == StructureLevelKind.Repeated ? Offsets![^1] : 0;

    /// <summary>The arrays are used as they are, not copied.</summary>
    public static StructureLevel ForOptional(SchemaNode node, bool[] validity)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(validity);
        if (node.Repetition != FieldRepetitionType.Optional)
        {
            throw new ArgumentException($"Node '{node.Name}' is not optional.", nameof(node));
        }

        return new StructureLevel(node, StructureLevelKind.Optional, validity, null);
    }

    /// <summary>The arrays are used as they are, not copied.</summary>
    public static StructureLevel ForRepeated(SchemaNode node, int[] offsets)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(offsets);
        if (node.Repetition != FieldRepetitionType.Repeated)
        {
            throw new ArgumentException($"Node '{node.Name}' is not repeated.", nameof(node));
        }

        if (offsets.Length == 0 || offsets[0] != 0)
        {
            throw new ArgumentException("The offsets must start with 0 and have one entry more than there are items.", nameof(offsets));
        }

        for (int i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new ArgumentException($"The offsets must not decrease, but offset {i} ({offsets[i]}) is below offset {i - 1} ({offsets[i - 1]}).", nameof(offsets));
            }
        }

        return new StructureLevel(node, StructureLevelKind.Repeated, null, offsets);
    }
}
