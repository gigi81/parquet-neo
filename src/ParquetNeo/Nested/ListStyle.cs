namespace ParquetNeo.Nested;

/// <summary>How a list is laid out in the schema. Writers have used several; readers must accept them all.</summary>
public enum ListStyle
{
    /// <summary>The standard layout: a LIST group holding a repeated group that holds the element (which may be optional).</summary>
    ThreeLevel,

    /// <summary>A LIST group holding a repeated primitive, which is the element (required).</summary>
    RepeatedPrimitive,

    /// <summary>A LIST group holding a repeated group with several fields, which is the element (a struct, required).</summary>
    RepeatedStruct,

    /// <summary>A LIST group holding a repeated group with one field named <c>array</c> or <c>{list}_tuple</c>, which is the element (a struct, required).</summary>
    RepeatedTuple,

    /// <summary>A repeated field outside any LIST group: a list of required elements of the field's type.</summary>
    Implicit,
}
