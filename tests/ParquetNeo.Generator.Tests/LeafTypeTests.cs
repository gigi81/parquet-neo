using ParquetNeo.ColumnTypes;
using ParquetNeo.Generator;

namespace ParquetNeo.Generator.Tests;

/// <summary>The generator has its own list of the types that have a column of their own, since it cannot ask the library: the two must not drift apart.</summary>
public class LeafTypeTests
{
    [Test]
    public async Task EveryTypeInTheGeneratorsList_HasAColumnTypeInTheLibrary()
    {
        foreach (string name in LeafTypes.MetadataNames)
        {
            var type = Type.GetType(name, throwOnError: true)!;

            await Assert.That(ColumnTypeRegistry.IsSupported(type)).IsTrue();
        }
    }

    [Test]
    public async Task EveryBuiltInColumnTypeOfTheLibrary_IsInTheGeneratorsList()
    {
        var builtIn = ColumnTypeRegistry.GetBuiltInValueTypes().Select(t => t.IsArray ? "System.Byte[]" : t.FullName!).OrderBy(n => n).ToArray();

        await Assert.That(builtIn).IsEquivalentTo(LeafTypes.MetadataNames.OrderBy(n => n).ToArray());
    }
}
