using System;
using Microsoft.CodeAnalysis;

namespace ParquetNeo.Generator
{
    internal static class Descriptors
    {
        public const string NotGeneratedId = "PNEO001";
        public const string NotReadableId = "PNEO002";

        private static readonly DiagnosticDescriptor NotGenerated = new DiagnosticDescriptor(
            NotGeneratedId,
            "A type has no generated mapping",
            "No mapping is generated for {0}: {1}. It keeps working through reflection, which is not safe with trimming or Native AOT.",
            "ParquetNeo",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor NotReadable = new DiagnosticDescriptor(
            NotReadableId,
            "Rows of a type cannot be read through a generated mapping",
            "Rows of {0} can be written through a generated mapping, but not read: {1}. Reading keeps working through reflection, which is not safe with trimming or Native AOT.",
            "ParquetNeo",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static DiagnosticDescriptor Get(string id)
        {
            switch (id)
            {
                case NotGeneratedId:
                    return NotGenerated;
                case NotReadableId:
                    return NotReadable;
                default:
                    throw new ArgumentOutOfRangeException(nameof(id));
            }
        }
    }
}
