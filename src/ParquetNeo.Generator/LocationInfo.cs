using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ParquetNeo.Generator
{
    /// <summary>Where something is in the source, in a form that is equal to another of the same place (a <see cref="Location"/> is not).</summary>
    internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public static LocationInfo? From(Location? location) =>
            location?.SourceTree is { } tree ? new LocationInfo(tree.FilePath, location.SourceSpan, location.GetLineSpan().Span) : null;

        public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
    }
}
