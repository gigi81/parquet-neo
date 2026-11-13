namespace ParquetNeo.Generator
{
    /// <summary>The result of looking at one place that asks for a mapping: the model of the type, when it can have one, and what to tell the user.</summary>
    internal sealed record AnalysisResult(MappingModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);
}
