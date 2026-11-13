using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ParquetNeo.Generator
{
    /// <summary>
    /// Writes the mapping of every type marked <c>[ParquetSerializable]</c> (or named by one on the assembly): the code the serializer
    /// would otherwise work out by reflection and compile when the type is first used, so that it works with trimming and Native AOT.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ParquetSerializableGenerator : IIncrementalGenerator
    {
        private const string AttributeName = "ParquetNeo.DataModel.ParquetSerializableAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var results = context.SyntaxProvider
                .ForAttributeWithMetadataName(AttributeName, static (_, _) => true, static (attribute, _) => Analyze(attribute))
                .Collect();
            context.RegisterSourceOutput(results, static (output, all) => Emit(output, all));
        }

        private static EquatableArray<AnalysisResult> Analyze(GeneratorAttributeSyntaxContext context)
        {
            var results = ImmutableArray.CreateBuilder<AnalysisResult>();
            foreach (var attribute in context.Attributes)
            {
                var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation());
                var analyzer = new TypeAnalyzer(context.SemanticModel.Compilation, location);
                var target = context.TargetSymbol as INamedTypeSymbol;
                if (attribute.ConstructorArguments.Length == 1)
                {
                    target = attribute.ConstructorArguments[0].Value as INamedTypeSymbol;
                }

                if (target is null)
                {
                    var failure = new DiagnosticInfo(Descriptors.NotGeneratedId, "?", "the attribute on the assembly must name the type, as [assembly: ParquetSerializable(typeof(TheType))]", location);
                    results.Add(new AnalysisResult(null, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(failure))));
                    continue;
                }

                results.Add(analyzer.Analyze(target));
            }

            return new EquatableArray<AnalysisResult>(results.ToImmutable());
        }

        private static void Emit(SourceProductionContext output, ImmutableArray<EquatableArray<AnalysisResult>> all)
        {
            var models = new SortedDictionary<string, MappingModel>(System.StringComparer.Ordinal);
            foreach (var result in all.SelectMany(group => group))
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    output.ReportDiagnostic(diagnostic.ToDiagnostic());
                }

                if (result.Model is { } model && !models.ContainsKey(model.TypeName))
                {
                    models.Add(model.TypeName, model);
                }
            }

            if (models.Count > 0)
            {
                output.AddSource("ParquetNeo.Mappings.g.cs", SourceText.From(MappingWriter.Write(models.Values.ToList()), Encoding.UTF8));
            }
        }
    }
}
