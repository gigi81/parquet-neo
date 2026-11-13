using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace ParquetNeo.Generator
{
    /// <summary>The types that are a column of their own, which the generator has to know since it cannot ask the library's registry.</summary>
    internal static class LeafTypes
    {
        /// <summary>The metadata names of the built-in column types of the library, which a test compares with its registry. Enums are one more kind.</summary>
        public static readonly IReadOnlyList<string> MetadataNames = new[]
        {
            "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64",
            "System.UInt64", "System.Single", "System.Double", "System.Decimal", "System.String", "System.DateTime", "System.DateTimeOffset",
            "System.Half", "System.Guid", "System.DateOnly", "System.TimeOnly", "System.Byte[]",
        };

        private static readonly HashSet<string> Names = new HashSet<string>(MetadataNames);

        /// <summary>Whether values of the type are stored in a column of their own, which is so for the built-in types and enums.</summary>
        public static bool IsLeaf(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            return MetadataNameOf(type) is { } name && Names.Contains(name);
        }

        public static string? MetadataNameOf(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
            {
                return "System.Byte[]";
            }

            return type is INamedTypeSymbol { ContainingNamespace: { } space, TypeArguments.Length: 0 } named && !named.IsAnonymousType
                ? space.ToDisplayString() + "." + named.MetadataName
                : null;
        }
    }
}
