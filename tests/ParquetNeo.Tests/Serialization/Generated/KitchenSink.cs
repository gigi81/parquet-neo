using ParquetNeo.DataModel;

namespace ParquetNeo.Tests.Serialization.Generated;

/// <summary>The type with a generated mapping; its own properties come after those of the base.</summary>
[ParquetSerializable]
public sealed class KitchenSink : KitchenSinkBase
{
    public string Own { get; set; } = "";

    public int? OwnMaybe { get; init; }
}
