namespace ParquetNeo.Linq;

/// <summary>What the query of a dataset starts from.</summary>
internal interface IDatasetRoot
{
    Type ElementType { get; }
}
