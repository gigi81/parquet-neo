using System.Reflection;

namespace ParquetNeo.Enums.Tests;

/// <summary>The enumerations are in an assembly of their own that depends on nothing, so that they stay apart from the classes that use them.</summary>
public class EnumsAssemblyTests
{
    private static readonly Assembly Enums = typeof(PhysicalType).Assembly;

    [Test]
    public async Task TheAssembly_HoldsOnlyEnums_AllInTheParquetNeoNamespace()
    {
        var types = Enums.GetTypes();

        await Assert.That(Enums.GetName().Name).IsEqualTo("ParquetNeo.Enums");
        await Assert.That(types.All(t => t.IsEnum)).IsTrue();
        await Assert.That(types.All(t => t.Namespace == "ParquetNeo")).IsTrue();
        await Assert.That(types.Count).IsGreaterThanOrEqualTo(14);
    }

    [Test]
    public async Task TheAssembly_IsPublic_SoEveryEnumCanBeUsedByTheLibrariesAndTheirUsers()
    {
        await Assert.That(Enums.GetTypes().All(t => t.IsPublic)).IsTrue();
    }

    [Test]
    public async Task TheAssembly_DependsOnNothingButTheRuntime()
    {
        var references = Enums.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        await Assert.That(references.Any(name => name.StartsWith("ParquetNeo", StringComparison.Ordinal))).IsFalse();
        await Assert.That(references).IsEquivalentTo(["System.Runtime"]);
    }
}
