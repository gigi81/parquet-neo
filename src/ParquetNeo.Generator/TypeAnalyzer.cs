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
    /// columns, in which order, of which kind, whether they can have nulls, and what objects, collections and dictionaries are made
    /// of. The two must agree, since a file written by one is read by the other; the tests of the library compare them.
    /// </summary>
    internal sealed class TypeAnalyzer
    {
        private const string AttributeNamespace = "ParquetNeo.DataModel.";

        private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private readonly Compilation _compilation;
        private readonly LocationInfo? _location;
        private readonly Dictionary<string, ObjectModel> _objects = new Dictionary<string, ObjectModel>(StringComparer.Ordinal);
        private readonly List<ObjectModel> _ordered = new List<ObjectModel>();
        private readonly List<INamedTypeSymbol> _stack = new List<INamedTypeSymbol>();

        public TypeAnalyzer(Compilation compilation, LocationInfo? location)
        {
            _compilation = compilation;
            _location = location;
        }

        /// <summary>The model of the type, or what stops it from having one.</summary>
        public AnalysisResult Analyze(INamedTypeSymbol type)
        {
            string typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            try
            {
                var root = AnalyzeObject(type);
                var diagnostics = ImmutableArray<DiagnosticInfo>.Empty;
                if (root.ReadProblem != null)
                {
                    diagnostics = diagnostics.Add(new DiagnosticInfo(Descriptors.NotReadableId, typeName, root.ReadProblem, _location));
                }

                var model = new MappingModel(root.Type, new EquatableArray<ObjectModel>(_ordered.ToImmutableArray()));
                return new AnalysisResult(model, new EquatableArray<DiagnosticInfo>(diagnostics));
            }
            catch (UnmappableTypeException failure)
            {
                var diagnostic = new DiagnosticInfo(Descriptors.NotGeneratedId, typeName, failure.Message, _location);
                return new AnalysisResult(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(diagnostic)));
            }
        }

        private ObjectModel AnalyzeObject(INamedTypeSymbol type)
        {
            string text = Text(type);
            if (_objects.TryGetValue(text, out var known))
            {
                return known;
            }

            if (_stack.Any(open => SymbolEqualityComparer.Default.Equals(open, type)))
            {
                throw new UnmappableTypeException($"{type.Name} contains itself, which a file with a fixed schema cannot hold");
            }

            RequireMappable(type);
            _stack.Add(type);
            var properties = new List<PropertyModel>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in Discover(type))
            {
                var column = CreateProperty(property);
                if (!names.Add(column.Name))
                {
                    throw new UnmappableTypeException($"two properties of {type.Name} are written to a column named '{column.Name}'");
                }

                properties.Add(column);
            }

            _stack.RemoveAt(_stack.Count - 1);
            if (properties.Count == 0)
            {
                throw new UnmappableTypeException($"{type.Name} has no public properties to write as columns");
            }

            string? readProblem = WhyNotReadable(type, properties);
            var model = new ObjectModel(text, new EquatableArray<PropertyModel>(properties.ToImmutableArray()), readProblem);
            _objects.Add(text, model);
            _ordered.Add(model);
            return model;
        }

        private void RequireMappable(INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
            {
                throw new UnmappableTypeException($"{type.Name} is not a class or a struct");
            }

            if (ContainsTypeParameter(type))
            {
                throw new UnmappableTypeException($"{type.Name} is a generic type that is not closed");
            }

            if (!IsAccessible(type))
            {
                throw new UnmappableTypeException($"{type.Name} is not accessible from the assembly that asks for the mapping, so generated code cannot name it");
            }
        }

        private static bool ContainsTypeParameter(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol _:
                    return true;
                case IArrayTypeSymbol array:
                    return ContainsTypeParameter(array.ElementType);
                case INamedTypeSymbol named:
                    return named.TypeArguments.Any(ContainsTypeParameter) || (named.ContainingType != null && ContainsTypeParameter(named.ContainingType));
                default:
                    return false;
            }
        }

        /// <summary>Why rows of the object cannot be created by generated code, or null: it includes why the objects inside it cannot.</summary>
        private static string? WhyNotReadable(INamedTypeSymbol type, List<PropertyModel> properties)
        {
            var settable = properties.Where(p => p.CanSet).ToList();
            if (settable.Count == 0)
            {
                return $"{type.Name} has no public properties with a setter to read into";
            }

            if (type.IsAbstract)
            {
                return $"{type.Name} is abstract";
            }

            if (type.TypeKind == TypeKind.Class
                && !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            {
                return $"{type.Name} has no public constructor without parameters (a type built by a constructor is not supported yet)";
            }

            return settable.Select(p => p.Shape.ReadProblem).FirstOrDefault(problem => problem != null);
        }

        private bool IsAccessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);

        private PropertyModel CreateProperty(IPropertySymbol property)
        {
            var attributes = ReadAttributes(property);
            string described = $"Property '{property.ContainingType.Name}.{property.Name}'";
            var shape = BuildShape(property.Type, property.Type.NullableAnnotation, attributes, described, lenient: false);
            bool canSet = property.SetMethod != null && property.SetMethod.DeclaredAccessibility == Accessibility.Public;
            return new PropertyModel(attributes.Name ?? property.Name, property.Name, described, canSet, shape);
        }

        /// <summary>The shape of a value of a type, whatever kind of column or columns it is stored as.</summary>
        /// <param name="lenient">Whether an attribute that does not suit the type is ignored instead of refused: the keys and values of a dictionary share the attributes of its property, which can suit just one of them.</param>
        private ShapeModel BuildShape(ITypeSymbol declared, NullableAnnotation annotation, PropertyAttributes attributes, string described, bool lenient)
        {
            var type = declared;
            bool nullableValue = false;
            bool optional;
            if (declared is INamedTypeSymbol { OriginalDefinition: { SpecialType: SpecialType.System_Nullable_T } } nullable)
            {
                type = nullable.TypeArguments[0];
                nullableValue = true;
                optional = true;
            }
            else
            {
                // A reference type can have nulls unless it says it cannot: an unannotated one (no nullable context) counts as one that can.
                optional = !declared.IsValueType && annotation != NullableAnnotation.NotAnnotated;
            }

            if (ContainsTypeParameter(type))
            {
                throw new UnmappableTypeException($"{described} has a type that is not closed");
            }

            if (LeafTypes.IsLeaf(type))
            {
                return BuildLeaf(type, optional, nullableValue, attributes, described, lenient);
            }

            if (IsSystemType(type) && !(type is IArrayTypeSymbol) && !TryGetDictionary(type, out _, out _) && !TryGetItem(type, out _))
            {
                throw new UnmappableTypeException($"{described} is a {type.ToDisplayString()}, which cannot be written as a column");
            }

            if (TryGetDictionary(type, out var keyType, out var valueType))
            {
                return BuildMap(type, keyType!, valueType!, optional, attributes, described, lenient);
            }

            if (TryGetItem(type, out var itemType))
            {
                return BuildList(type, itemType!, optional, attributes, described, lenient);
            }

            return BuildObject(type, optional, nullableValue, attributes, described, lenient);
        }

        private ShapeModel BuildLeaf(ITypeSymbol type, bool optional, bool nullableValue, PropertyAttributes attributes, string described, bool lenient)
        {
            if (type.TypeKind == TypeKind.Enum && !IsAccessible(type))
            {
                throw new UnmappableTypeException($"the enum {type.ToDisplayString()} of {described} is not accessible from the assembly that asks for the mapping");
            }

            string? settings = CreateSettings(attributes, type, described, lenient);
            return new ShapeModel(
                ShapeKind.Leaf, Text(type), optional, type.IsValueType, nullableValue, 1, 1, type.TypeKind == TypeKind.Enum, settings, attributes.Encoding, attributes.Dictionary, null, null, null, null);
        }

        private ShapeModel BuildObject(ITypeSymbol type, bool optional, bool nullableValue, PropertyAttributes attributes, string described, bool lenient)
        {
            if (!lenient && attributes.AdjustsAColumn)
            {
                throw new UnmappableTypeException(Misplaced(described, FirstAdjustment(attributes), type.Name));
            }

            if (!(type is INamedTypeSymbol named))
            {
                throw new UnmappableTypeException($"{described} is a {type.ToDisplayString()}, which cannot be written as a column");
            }

            var model = AnalyzeObject(named);
            return new ShapeModel(
                ShapeKind.Object, model.Type, optional, type.IsValueType, nullableValue, model.WriteColumns, model.ReadColumns, false, null, null, null, null, null, null, model.ReadProblem);
        }

        private ShapeModel BuildList(ITypeSymbol collection, ITypeSymbol item, bool optional, PropertyAttributes attributes, string described, bool lenient)
        {
            RequireClass(collection, described);
            var element = BuildShape(item, ElementAnnotation(collection, 1, 0), attributes, $"{described}, an element,", lenient);
            string itemText = element.Type + (element.IsNullableValue ? "?" : "");
            string? create = null;
            string? problem = element.ReadProblem;
            if (!MadeFromWhatWasRead(collection, item))
            {
                create = ListCreation(collection, itemText);
                problem = problem ?? (create == null ? $"{described} is a {collection.Name}, which cannot be created: it needs a public constructor without parameters and to be an ICollection<T>, or to be an interface a HashSet<T> implements" : null);
            }

            return new ShapeModel(
                ShapeKind.List, Text(collection), optional, false, false, element.WriteColumns, element.ReadColumns, false, null, null, null, element, null, create, problem);
        }

        private ShapeModel BuildMap(ITypeSymbol map, ITypeSymbol keyType, ITypeSymbol valueType, bool optional, PropertyAttributes attributes, string described, bool lenient)
        {
            RequireClass(map, described);
            if (keyType is INamedTypeSymbol { OriginalDefinition: { SpecialType: SpecialType.System_Nullable_T } })
            {
                throw new UnmappableTypeException($"{described} has keys of type {keyType.ToDisplayString()}, but the keys of a map cannot be null");
            }

            if (!lenient)
            {
                RejectUnusedAttributes(map, attributes, described);
            }

            var key = BuildShape(keyType, NullableAnnotation.NotAnnotated, attributes, $"{described}, a key,", lenient: true);
            var value = BuildShape(valueType, ElementAnnotation(map, 2, 1), attributes, $"{described}, a value,", lenient: true);
            string valueText = value.Type + (value.IsNullableValue ? "?" : "");
            string? create = MapCreation(map, Text(keyType), valueText);
            string? problem = key.ReadProblem ?? value.ReadProblem;
            if (create == null && problem == null)
            {
                problem = $"{described} is a {map.Name}, which cannot be created: it needs a public constructor without parameters and to be an IDictionary<K, V>, or to be an interface a Dictionary<K, V> implements";
            }

            return new ShapeModel(
                ShapeKind.Map, Text(map), optional, false, false, key.WriteColumns + value.WriteColumns, key.ReadColumns + value.ReadColumns, false, null, null, null, value, key, create, problem);
        }

        private static void RequireClass(ITypeSymbol type, string described)
        {
            if (type.IsValueType)
            {
                throw new UnmappableTypeException($"{described} is a {type.ToDisplayString()}, a collection that is a struct, which is not supported. Use an array, a List<T> or an interface");
            }
        }

        private static NullableAnnotation ElementAnnotation(ITypeSymbol collection, int arity, int index)
        {
            if (collection is IArrayTypeSymbol array)
            {
                return array.ElementNullableAnnotation;
            }

            // Where the element is not one of the type arguments (a collection class of its own), nothing says it cannot be null.
            return collection is INamedTypeSymbol named && named.TypeArguments.Length == arity ? named.TypeArgumentNullableAnnotations[index] : NullableAnnotation.None;
        }

        private static bool IsSystemType(ITypeSymbol type)
        {
            string? space = type.ContainingNamespace?.ToDisplayString();
            return space != null && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal) || space.StartsWith("Microsoft.", StringComparison.Ordinal));
        }

        private bool TryGetDictionary(ITypeSymbol type, out ITypeSymbol? key, out ITypeSymbol? value)
        {
            var definitions = new[]
            {
                _compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2"),
                _compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2"),
            };
            var found = Implemented(type, definitions);
            var pairs = new List<INamedTypeSymbol>();
            foreach (var candidate in found.Where(candidate => !pairs.Any(p => SymbolEqualityComparer.Default.Equals(p.TypeArguments[0], candidate.TypeArguments[0])
                && SymbolEqualityComparer.Default.Equals(p.TypeArguments[1], candidate.TypeArguments[1]))))
            {
                pairs.Add(candidate);
            }

            key = pairs.Count == 1 ? pairs[0].TypeArguments[0] : null;
            value = pairs.Count == 1 ? pairs[0].TypeArguments[1] : null;
            return pairs.Count == 1;
        }

        private bool TryGetItem(ITypeSymbol type, out ITypeSymbol? item)
        {
            if (type is IArrayTypeSymbol array)
            {
                item = array.Rank == 1 ? array.ElementType : null;
                return array.Rank == 1;
            }

            var found = Implemented(type, new[] { _compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1") });
            var items = new List<ITypeSymbol>();
            foreach (var candidate in found.Where(candidate => !items.Any(i => SymbolEqualityComparer.Default.Equals(i, candidate.TypeArguments[0]))))
            {
                items.Add(candidate.TypeArguments[0]);
            }

            item = items.Count == 1 ? items[0] : null;
            return items.Count == 1;
        }

        private static List<INamedTypeSymbol> Implemented(ITypeSymbol type, INamedTypeSymbol?[] definitions)
        {
            var all = type.AllInterfaces.AsEnumerable();
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } itself)
            {
                all = all.Append(itself);
            }

            return all.Where(i => i.IsGenericType && definitions.Any(d => d != null && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, d))).ToList();
        }

        /// <summary>Whether an array, a <c>List&lt;T&gt;</c> or an interface that one implements is read: those are made from the elements, collected first.</summary>
        private bool MadeFromWhatWasRead(ITypeSymbol collection, ITypeSymbol item)
        {
            if (collection is IArrayTypeSymbol)
            {
                return true;
            }

            var list = Construct("System.Collections.Generic.List`1", item);
            return list != null && (SymbolEqualityComparer.Default.Equals(collection, list) || (collection.TypeKind == TypeKind.Interface && IsImplicitlyConvertible(list, collection)));
        }

        private string? ListCreation(ITypeSymbol collection, string itemText)
        {
            if (collection.TypeKind == TypeKind.Interface)
            {
                var set = Construct("System.Collections.Generic.HashSet`1", ItemTypeOf(collection));
                return set != null && IsImplicitlyConvertible(set, collection) ? $"static () => new global::System.Collections.Generic.HashSet<{itemText}>()" : null;
            }

            bool creatable = collection is INamedTypeSymbol { IsAbstract: false } named
                && named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                && IsAccessible(named)
                && ImplementsCollection(collection);
            return creatable ? $"static () => new {Text(collection)}()" : null;
        }

        private string? MapCreation(ITypeSymbol map, string keyText, string valueText)
        {
            if (map.TypeKind == TypeKind.Interface)
            {
                var dictionary = _compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
                var constructed = dictionary?.Construct(((INamedTypeSymbol)map).TypeArguments.ToArray());
                bool fits = map is INamedTypeSymbol { TypeArguments: { Length: 2 } } && constructed != null && IsImplicitlyConvertible(constructed, map);
                return fits ? $"static () => new global::System.Collections.Generic.Dictionary<{keyText}, {valueText}>()" : null;
            }

            bool creatable = map is INamedTypeSymbol { IsAbstract: false } named
                && named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                && IsAccessible(named)
                && Implemented(map, new[] { _compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2") }).Count > 0;
            return creatable ? $"static () => new {Text(map)}()" : null;
        }

        private bool ImplementsCollection(ITypeSymbol collection) =>
            Implemented(collection, new[] { _compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1") }).Count > 0;

        private ITypeSymbol ItemTypeOf(ITypeSymbol collection)
        {
            TryGetItem(collection, out var item);
            return item!;
        }

        private INamedTypeSymbol? Construct(string metadataName, ITypeSymbol argument) =>
            _compilation.GetTypeByMetadataName(metadataName)?.Construct(argument);

        private bool IsImplicitlyConvertible(ITypeSymbol from, ITypeSymbol to)
        {
            var conversion = _compilation.ClassifyCommonConversion(from, to);
            return conversion.Exists && conversion.IsImplicit;
        }

        private static string Text(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

        /// <summary>The public readable properties that are written, in the order of their columns: by the order of the attribute, then base types before derived ones, then as declared.</summary>
        private static List<IPropertySymbol> Discover(INamedTypeSymbol type)
        {
            // The most derived declaration of a name counts: an override or a hiding property replaces the one it overrides.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<(IPropertySymbol Property, int Depth, int Index)>();
            for (var current = (INamedTypeSymbol?)type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
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

                    if (property.GetMethod != null && property.GetMethod.DeclaredAccessibility == Accessibility.Public && FindAttribute(property, "ParquetIgnoreAttribute") == null)
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
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                depth++;
            }

            return depth;
        }

        private static int OrderOf(IPropertySymbol property)
        {
            var attribute = FindAttribute(property, "ParquetColumnAttribute");
            return attribute != null && NamedArgument(attribute, "Order") is { Value: int order } ? order : 0;
        }

        /// <summary>The attribute on a property, or on the property it overrides: the attributes are inherited.</summary>
        private static AttributeData? FindAttribute(IPropertySymbol property, string name)
        {
            for (var current = (IPropertySymbol?)property; current != null; current = current.OverriddenProperty)
            {
                var found = current.GetAttributes().FirstOrDefault(a => IsAttribute(a, name));
                if (found != null)
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

        private static PropertyAttributes ReadAttributes(IPropertySymbol property)
        {
            var attributes = new PropertyAttributes
            {
                Decimal = FindAttribute(property, "ParquetDecimalAttribute"),
                Time = FindAttribute(property, "ParquetTimeAttribute"),
                Timestamp = FindAttribute(property, "ParquetTimestampAttribute"),
            };
            if (FindAttribute(property, "ParquetColumnAttribute") is { ConstructorArguments: { Length: 1 } } column)
            {
                attributes.Name = column.ConstructorArguments[0].Value as string;
            }

            if (FindAttribute(property, "ParquetEncodingAttribute") is { ConstructorArguments: { Length: 1 } } encoding)
            {
                attributes.Encoding = $"(global::ParquetNeo.ParquetEncoding){Number(encoding.ConstructorArguments[0].Value)}";
            }

            if (FindAttribute(property, "ParquetDictionaryAttribute") is { ConstructorArguments: { Length: 1 } } dictionary)
            {
                // Zero, the default of the attribute, leaves the limit of the writer.
                string limit = NamedArgument(dictionary, "SizeLimit") is { Value: int value } && value != 0 ? $", SizeLimit = {Number(value)}" : "";
                attributes.Dictionary = $"new global::ParquetNeo.Core.DictionarySettings {{ Mode = (global::ParquetNeo.DictionaryMode){Number(dictionary.ConstructorArguments[0].Value)}{limit} }}";
            }

            return attributes;
        }

        private static string FirstAdjustment(PropertyAttributes attributes)
        {
            if (attributes.Decimal != null)
            {
                return "ParquetDecimalAttribute";
            }

            return attributes.Time != null ? "ParquetTimeAttribute" : "ParquetTimestampAttribute";
        }

        /// <summary>Refuses a column attribute that suits none of the types a dictionary is made of, such as a decimal attribute on a dictionary of text.</summary>
        private void RejectUnusedAttributes(ITypeSymbol map, PropertyAttributes attributes, string described)
        {
            var leaves = new List<string>();
            CollectLeaves(map, leaves);
            foreach (var (attribute, name, needs, suits) in AttributeKinds(attributes))
            {
                if (attribute != null && !leaves.Any(suits))
                {
                    throw new UnmappableTypeException($"{described} has {name}, but none of what it is made of is {needs}");
                }
            }
        }

        /// <summary>The primitive types a type is made of, looking through collections and dictionaries but not into objects, whose properties have attributes of their own.</summary>
        private void CollectLeaves(ITypeSymbol type, List<string> leaves)
        {
            if (type is INamedTypeSymbol { OriginalDefinition: { SpecialType: SpecialType.System_Nullable_T } } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (LeafTypes.IsLeaf(type))
            {
                leaves.Add(LeafTypes.MetadataNameOf(type) ?? "");
            }
            else if (TryGetDictionary(type, out var key, out var value))
            {
                CollectLeaves(key!, leaves);
                CollectLeaves(value!, leaves);
            }
            else if (TryGetItem(type, out var item))
            {
                CollectLeaves(item!, leaves);
            }
        }

        private static IEnumerable<(AttributeData? Attribute, string Name, string Needs, Func<string, bool> Suits)> AttributeKinds(PropertyAttributes attributes)
        {
            yield return (attributes.Decimal, "ParquetDecimalAttribute", "a decimal", leaf => leaf == "System.Decimal");
            yield return (attributes.Time, "ParquetTimeAttribute", "a TimeOnly", leaf => leaf == "System.TimeOnly");
            yield return (attributes.Timestamp, "ParquetTimestampAttribute", "a DateTime or DateTimeOffset", leaf => leaf == "System.DateTime" || leaf == "System.DateTimeOffset");
        }

        /// <summary>The code of the settings the attributes of the property give a column type, or null when there are none; refused when an attribute does not suit the type.</summary>
        private static string? CreateSettings(PropertyAttributes attributes, ITypeSymbol type, string described, bool lenient)
        {
            string? metadataName = LeafTypes.MetadataNameOf(type);
            string? settings = null;
            foreach (var (attribute, name, needs, suits) in AttributeKinds(attributes))
            {
                if (attribute == null)
                {
                    continue;
                }

                if (metadataName == null || !suits(metadataName))
                {
                    if (lenient)
                    {
                        continue;
                    }

                    throw new UnmappableTypeException(Misplaced(described, name, type.Name, needs));
                }

                settings = name == "ParquetDecimalAttribute" ? DecimalSettings(attribute, described) : TimeSettings(attribute, name == "ParquetTimestampAttribute");
            }

            return settings == null ? null : $"new global::ParquetNeo.ColumnTypes.ColumnSettings {{ {settings} }}";
        }

        private static string DecimalSettings(AttributeData attribute, string described)
        {
            int digits = attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[0].Value is int p ? p : 0;
            int scale = attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is int s ? s : 0;
            if (digits < 1 || digits > 38 || scale < 0 || scale > Math.Min(digits, 28))
            {
                throw new UnmappableTypeException($"{described} has a ParquetDecimalAttribute with a precision of {digits} and a scale of {scale}, which is not valid");
            }

            return $"Precision = {Number(digits)}, Scale = {Number(scale)}";
        }

        private static string TimeSettings(AttributeData attribute, bool timestamp)
        {
            string unit = attribute.ConstructorArguments.Length == 1 ? $"(global::ParquetNeo.TimeUnit){Number(attribute.ConstructorArguments[0].Value)}" : "global::ParquetNeo.TimeUnit.Micros";
            bool utc = NamedArgument(attribute, "IsAdjustedToUtc") is { Value: bool given } ? given : timestamp;
            return $"Unit = {unit}, IsAdjustedToUtc = {(utc ? "true" : "false")}";
        }

        private static string Misplaced(string described, string attribute, string typeName, string? needs = null) =>
            $"{described} has {attribute}, which suits {needs ?? "another type"}, not {typeName}";

        private static string Number(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }
}
