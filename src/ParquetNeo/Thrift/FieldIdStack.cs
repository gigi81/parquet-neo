using System.Runtime.CompilerServices;

namespace ParquetNeo.Thrift;

/// <summary>Fixed-size stack of "last field id" values, one per open struct (no allocation).</summary>
[InlineArray(Size)]
internal struct FieldIdStack
{
    /// <summary>Maximum supported nesting depth of structs, lists and maps.</summary>
    public const int Size = 32;

    private short _element;
}
