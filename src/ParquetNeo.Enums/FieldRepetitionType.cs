namespace ParquetNeo;

/// <summary>Whether a field has a value in every row, in some, or any number of them (thrift <c>FieldRepetitionType</c>).</summary>
public enum FieldRepetitionType
{
    /// <summary>The field always has a value.</summary>
    Required = 0,
    /// <summary>The field has a value or is null.</summary>
    Optional = 1,
    /// <summary>The field has any number of values: a list.</summary>
    Repeated = 2,
}