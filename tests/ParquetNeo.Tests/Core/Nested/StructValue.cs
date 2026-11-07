namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// A group of the nested test data: field values by name. A field that is missing counts as null. Repeated fields
/// hold an <c>object?[]</c> of their elements.
/// </summary>
internal sealed class StructValue : Dictionary<string, object?>
{
}
