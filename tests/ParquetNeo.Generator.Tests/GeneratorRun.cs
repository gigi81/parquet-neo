using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ParquetNeo.Generator;

namespace ParquetNeo.Generator.Tests;

/// <summary>What the generator did with some source: the code it added, what it reported, and whether the result compiles.</summary>
internal sealed class GeneratorRun
{
    private static readonly ImmutableArray<MetadataReference> References = ReadReferences();

    private GeneratorRun(string? generated, ImmutableArray<Diagnostic> reported, ImmutableArray<Diagnostic> compileErrors)
    {
        Generated = generated;
        Reported = reported;
        CompileErrors = compileErrors;
    }

    /// <summary>The code the generator added, or null when it added none.</summary>
    public string? Generated { get; }

    /// <summary>What the generator reported, by the ids of the diagnostics.</summary>
    public ImmutableArray<Diagnostic> Reported { get; }

    /// <summary>The errors of compiling the source together with the code the generator added.</summary>
    public ImmutableArray<Diagnostic> CompileErrors { get; }

    public IEnumerable<string> ReportedIds => Reported.Select(d => d.Id);

    public static GeneratorRun Of(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Sample.cs");
        var compilation = CSharpCompilation.Create(
            "Tests",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ParquetSerializableGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics);
        var result = driver.GetRunResult();
        string? generated = result.GeneratedTrees.Length == 0 ? null : result.GeneratedTrees[0].GetText().ToString();
        var errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return new GeneratorRun(generated, generatorDiagnostics, errors);
    }

    private static ImmutableArray<MetadataReference> ReadReferences()
    {
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return paths.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }
}
