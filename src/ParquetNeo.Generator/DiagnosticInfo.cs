using Microsoft.CodeAnalysis;

namespace ParquetNeo.Generator
{
    /// <summary>A diagnostic to report, in a form that is equal to another of the same kind and place.</summary>
    internal sealed record DiagnosticInfo(string Id, string TypeName, string Reason, LocationInfo? Location)
    {
        public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptors.Get(Id), Location?.ToLocation(), TypeName, Reason);
    }
}
