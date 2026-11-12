namespace ParquetNeo;

/// <summary>What a <c>StructureLevel</c> records about its schema node.</summary>
public enum StructureLevelKind
{
    /// <summary>An optional node: for each item, whether the value is present.</summary>
    Optional,

    /// <summary>A repeated node: for each item of the enclosing level, where its elements start and end.</summary>
    Repeated,
}
