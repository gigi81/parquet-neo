using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ParquetNeo.Thrift;

/// <summary>Fixed-size stack of "last field id" values, one per open struct (no allocation).</summary>
[InlineArray(Size)]
internal struct FieldIdStack
{
    /// <summary>Maximum supported nesting depth of structs, lists and maps.</summary>
    public const int Size = 32;

    [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "The single field of an [InlineArray]; the runtime lays out Size of them, and they are accessed as a span.")]
    private short _element;
}
