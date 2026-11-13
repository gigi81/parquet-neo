using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ParquetNeo.Generator
{
    /// <summary>
    /// Works out from the symbols of a type what the reflection path would work out from its <c>System.Type</c>: which properties are
    /// columns, in which order, of which kind, and whether they can have nulls. The two must agree, since a file written by one is
    /// read by the other; the tests of the library compare them.
    /// </summary>
    internal sealed class TypeAnalyzer
    {
        private const string AttributeNamespace = "ParquetNeo.DataModel.";

        private readonly Compilation _compilation;
        private readonly LocationInfo? _location;

        public TypeAnalyzer(Compilation compilation, LocationInfo? location)
        {
            _compilation = compilation;
            _location = location;
        }

        /// <summary>The model of the type, or what stops it from having one.</summary>
        public AnalysisResult Analyze(INamedTypeSymbol type)
        {
            string typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            if (WhyNotMappable(type) is { } why)
            {
                return Failed(Descriptors.NotGeneratedId, typeName, why);
            }

            var write = new List<ColumnModel>();
            var read = new List<ColumnModel>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in Discover(type))
            {
                var column = CreateColumn(property, out string? problem);
                if (column is null)
                {
                    return Failed(Descriptors.NotGeneratedId, typeName, problem!);
                }

                if (!names.Add(column.Name))
                {
                    return Failed(Descriptors.NotGeneratedId, typeName, $"two properties are written to a column named '{column.Name}'");
                }

                write.Add(column);
                if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public })
                {
                    read.Add(column);
                }
            }

            if (write.Count == 0)
            {
                return Failed(Descriptors.NotGeneratedId, typeName, "it has no public properties to write as columns");
            }

            var diagnostics = ImmutableArray<DiagnosticInfo>.Empty;
            if (WhyNotReadable(type, read.Count) is { } unreadable)
            {
                read.Clear();
                diagnostics = diagnostics.Add(new DiagnosticInfo(Descriptors.NotReadableId, typeName, unreadable, _location));
            }

            string display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var model = new MappingModel(display, ToArray(write), ToArray(read));
            return new AnalysisResult(model, new EquatableArray<DiagnosticInfo>(diagnostics));
        }

        private static EquatableArray<ColumnModel> ToArray(List<ColumnModel> columns) => new EquatableArray<ColumnModel>(columns.ToImmutableArray());

        private AnalysisResult Failed(string id, string typeName, string reason) =>
            new AnalysisResult(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(new DiagnosticInfo(id, typeName, reason, _location))));

        private string? WhyNotMappable(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
            {
                return "it is not a class or a struct";
            }

            if (type.IsGenericType)
            {
                return "it is a generic type";
            }

            if (!IsAccessible(type))
            {
                return "it is not accessible from the assembly that asks for the mapping, so generated code cannot name it";
            }

            return null;
        }

        private static string? WhyNotReadable(INamedTypeSymbol type, int settable)
        {
            if (settable == 0)
            {
                return "it has no public properties with a setter to read into";
            }

            if (type.IsAbstract)
            {
                return "it is abstract";
            }

            if (type.TypeKind == TypeKind.Class
                && !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            {
                return "it has no public constructor without parameters (a type built by a constructor is not supported yet)";
            }

            return null;
        }

        private bool IsAccessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);

        /// <summary>The public readable properties that are written, in the order of their columns: by the order of the attribute, then base types before derived ones, then as declared.</summary>
        private static List<IPropertySymbol> Discover(INamedTypeSymbol type)
        {
            // The most derived declaration of a name counts: an override or a hiding property replaces the one it overrides.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<(IPropertySymbol Property, int Depth, int Index)>();
            for (var current = (INamedTypeSymbol?)type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                int depth = Depth(current);
                int index = 0;
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    index++;
                    if (property.IsStatic || property.IsIndexer || property.ExplicitInterfaceImplementations.Length > 0 || !seen.Add(property.Name))
                    {
                        continue;
                    }

                    if (property.GetMethod is { DeclaredAccessibility: Accessibility.Public } && FindAttribute(property, "ParquetIgnoreAttribute") is null)
                    {
                        found.Add((property, depth, index));
                    }
                }
            }

            return found
                .OrderBy(f => OrderOf(f.Property))
                .ThenBy(f => f.Depth)
                .ThenBy(f => f.Index)
                .Select(f => f.Property)
                .ToList();
        }

        private static int Depth(INamedTypeSymbol type)
        {
            int depth = 0;
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                depth++;
            }

            return depth;
        }

        private static int OrderOf(IPropertySymbol property)
        {
            var attribute = FindAttribute(property, "ParquetColumnAttribute");
            return attribute is not null && NamedArgument(attribute, "Order") is { Value: int order } ? order : 0;
        }

        /// <summary>The attribute on a property, or on the property it overrides: the attributes are inherited.</summary>
        private static AttributeData? FindAttribute(IPropertySymbol property, string name)
        {
            for (var current = (IPropertySymbol?)property; current is not null; current = current.OverriddenProperty)
            {
                var found = current.GetAttributes().FirstOrDefault(a => IsAttribute(a, name));
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool IsAttribute(AttributeData attribute, string name) =>
            attribute.AttributeClass is { } type && type.Name == name && type.ContainingNamespace.ToDisplayString() + "." == AttributeNamespace;

        private static TypedConstant? NamedArgument(AttributeData attribute, string name)
        {
            foreach (var pair in attribute.NamedArguments)
            {
                if (pair.Key == name)
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private ColumnModel? CreateColumn(IPropertySymbol property, out string? problem)
        {
            problem = null;
            var type = property.Type;
            bool optional;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
                optional = true;
            }
            else
            {
                // A reference type can have nulls unless it says it cannot: an unannotated one (no nullable context) counts as one that can.
                optional = !type.IsValueType && property.NullableAnnotation != NullableAnnotation.NotAnnotated;
            }

            string described = $"Property '{property.ContainingType.Name}.{property.Name}'";
            if (!LeafTypes.IsLeaf(type))
            {
                problem = $"{described.Replace("Property", "property")} is a {type.ToDisplayString()}, which is not a type with a column of its own (objects and collections are not supported yet)";
                return null;
            }

            if (type.TypeKind == TypeKind.Enum && !IsAccessible(type))
            {
                problem = $"the enum {type.ToDisplayString()} of {described} is not accessible from the assembly that asks for the mapping";
                return null;
            }

            string? name = FindAttribute(property, "ParquetColumnAttribute") is { ConstructorArguments.Length: 1 } column ? column.ConstructorArguments[0].Value as string : null;
            var settings = CreateSettings(property, type, described, out problem);
            if (problem is not null)
            {
                return null;
            }

            var withoutAnnotation = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            return new ColumnModel(
                name ?? property.Name,
                property.Name,
                described,
                withoutAnnotation.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                optional,
                type.TypeKind == TypeKind.Enum,
                settings,
                CreateEncoding(property),
                CreateDictionary(property));
        }

        private static string? CreateEncoding(IPropertySymbol property) =>
            FindAttribute(property, "ParquetEncodingAttribute") is { ConstructorArguments.Length: 1 } attribute
                ? $"(global::ParquetNeo.ParquetEncoding){Number(attribute.ConstructorArguments[0].Value)}"
                : null;

        private static string? CreateDictionary(IPropertySymbol property)
        {
            if (FindAttribute(property, "ParquetDictionaryAttribute") is not { ConstructorArguments.Length: 1 } attribute)
            {
                return null;
            }

            // Zero, the default of the attribute, leaves the limit of the writer.
            string limit = NamedArgument(attribute, "SizeLimit") is { Value: int value } && value != 0 ? $", SizeLimit = {Number(value)}" : "";
            return $"new global::ParquetNeo.Core.DictionarySettings {{ Mode = (global::ParquetNeo.DictionaryMode){Number(attribute.ConstructorArguments[0].Value)}{limit} }}";
        }

        /// <summary>The code of the settings the attributes of the property give its column type, or null when there are none; a problem when an attribute does not suit the type.</summary>
        private static string? CreateSettings(IPropertySymbol property, ITypeSymbol type, string described, out string? problem)
        {
            problem = null;
            string? metadataName = LeafTypes.MetadataNameOf(type);
            string? settings = null;

            if (FindAttribute(property, "ParquetDecimalAttribute") is { ConstructorArguments.Length: 2 } precision)
            {
                if (metadataName != "System.Decimal")
                {
                    problem = Misplaced(described, "ParquetDecimalAttribute", "decimal");
                    return null;
                }

                int digits = precision.ConstructorArguments[0].Value is int p ? p : 0;
                int scale = precision.ConstructorArguments[1].Value is int s ? s : 0;
                if (digits < 1 || digits > 38 || scale < 0 || scale > Math.Min(digits, 28))
                {
                    problem = $"{described} has a ParquetDecimalAttribute with a precision of {digits} and a scale of {scale}, which is not valid";
                    return null;
                }

                settings = $"Precision = {Number(digits)}, Scale = {Number(scale)}";
            }

            foreach (var (attributeName, suits, expected) in new[]
            {
                ("ParquetTimeAttribute", "System.TimeOnly", "TimeOnly"),
                ("ParquetTimestampAttribute", "System.DateTime", "DateTime and DateTimeOffset"),
            })
            {
                if (FindAttribute(property, attributeName) is not { } attribute)
                {
                    continue;
                }

                bool fits = metadataName == suits || (attributeName == "ParquetTimestampAttribute" && metadataName == "System.DateTimeOffset");
                if (!fits)
                {
                    problem = Misplaced(described, attributeName, expected);
                    return null;
                }

                string unit = attribute.ConstructorArguments.Length == 1 ? $"(global::ParquetNeo.TimeUnit){Number(attribute.ConstructorArguments[0].Value)}" : "global::ParquetNeo.TimeUnit.Micros";
                bool utc = NamedArgument(attribute, "IsAdjustedToUtc") is { Value: bool given } ? given : attributeName == "ParquetTimestampAttribute";
                settings = $"Unit = {unit}, IsAdjustedToUtc = {(utc ? "true" : "false")}";
            }

            return settings is null ? null : $"new global::ParquetNeo.ColumnTypes.ColumnSettings {{ {settings} }}";
        }

        private static string Misplaced(string described, string attribute, string suits) => $"{described} has {attribute}, which suits {suits}";

        private static string Number(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }
}
