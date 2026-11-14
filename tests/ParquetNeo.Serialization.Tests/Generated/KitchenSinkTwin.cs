namespace ParquetNeo.Serialization.Tests.Generated;

/// <summary>The same as <see cref="KitchenSink"/>, without a generated mapping: it is found by reflection.</summary>
public sealed class KitchenSinkTwin : KitchenSinkBase
{
    public string Own { get; set; } = "";

    public int? OwnMaybe { get; init; }
}
