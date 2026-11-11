using System.Reflection;
using ParquetNeo.Core;
using ParquetNeo.Compression;

namespace ParquetNeo.Tests;

/// <summary>The enumerations are in an assembly of their own that depends on nothing, so that they stay apart from the classes that use them.</summary>
public class EnumsProjectTests
{
    private static readonly Assembly Enums = typeof(PhysicalType).Assembly;

    private static readonly Assembly[] LibraryAssemblies =
    [
        typeof(ParquetFileWriter).Assembly,
        typeof(ICodec).Assembly,
    ];

    [Test]
    public async Task TheEnumsAssembly_HoldsOnlyEnums_AllInTheParquetNeoNamespace()
    {
        var types = Enums.GetTypes();

        await Assert.That(Enums.GetName().Name).IsEqualTo("ParquetNeo.Enums");
        await Assert.That(types.All(t => t.IsEnum)).IsTrue();
        await Assert.That(types.All(t => t.Namespace == "ParquetNeo")).IsTrue();
        await Assert.That(types.Count).IsGreaterThanOrEqualTo(14);
    }

    [Test]
    public async Task TheEnumsAssembly_DependsOnNothingButTheRuntime()
    {
        var references = Enums.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        await Assert.That(references.Any(name => name.StartsWith("ParquetNeo", StringComparison.Ordinal))).IsFalse();
        await Assert.That(references).IsEquivalentTo(["System.Runtime"]);
    }

    [Test]
    public async Task NoPublicEnum_IsDeclaredOutsideTheEnumsAssembly()
    {
        var elsewhere = LibraryAssemblies.SelectMany(a => a.GetExportedTypes()).Where(t => t.IsEnum).Select(t => t.FullName).ToList();

        await Assert.That(elsewhere).IsEmpty();
    }
}
