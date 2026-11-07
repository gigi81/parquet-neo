using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>The levels and values of one leaf column after shredding.</summary>
internal sealed record ShreddedColumn(PrimitiveNode Column)
{
    public List<int> Definition { get; } = new();

    public List<int> Repetition { get; } = new();

    /// <summary>The non-null values, in order: <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="string"/> or <c>byte[]</c>.</summary>
    public List<object> Values { get; } = new();

    public int RowCount => Repetition.Count(level => level == 0);
}
