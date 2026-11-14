using System.Diagnostics.CodeAnalysis;
using Azure;

namespace ParquetNeo.Storage.AzureBlobs.Tests;

/// <summary>The raw response that the Azure SDK wraps a value in; the tests do not look into it.</summary>
public sealed class FakeResponse(int status) : Response
{
    public override int Status => status;

    public override string ReasonPhrase => "fake";

    public override Stream? ContentStream { get; set; }

    public override string ClientRequestId { get; set; } = "test";

    public override void Dispose()
    {
    }

    protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        return false;
    }

    protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        values = null;
        return false;
    }

    protected override bool ContainsHeader(string name) => false;

    protected override IEnumerable<Azure.Core.HttpHeader> EnumerateHeaders() => [];
}
