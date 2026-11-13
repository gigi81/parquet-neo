using Microsoft.CodeAnalysis;

namespace ParquetNeo.Generator
{
    /// <summary>The attributes of a property that adjust its columns.</summary>
    internal sealed class PropertyAttributes
    {
        public string? Name { get; set; }

        public AttributeData? Decimal { get; set; }

        public AttributeData? Time { get; set; }

        public AttributeData? Timestamp { get; set; }

        public string? Encoding { get; set; }

        public string? Dictionary { get; set; }

        public bool AdjustsAColumn => Decimal != null || Time != null || Timestamp != null;
    }
}
