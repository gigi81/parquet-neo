using System.Reflection;
using ParquetNeo.Compression;

namespace ParquetNeo.Tests;

/// <summary>The enumerations are in the ParquetNeo.Enums assembly (which has its own tests): the library declares none of its own.</summary>
public class EnumsProjectTests
{
    private static readonly Assembly[] LibraryAssemblies =
    [
        typeof(ParquetFileWriter).Assembly,
        typeof(ICodec).Assembly,
    ];

    [Test]
    public async Task NoPublicEnum_IsDeclaredOutsideTheEnumsAssembly()
    {
        var elsewhere = LibraryAssemblies.SelectMany(a => a.GetExportedTypes()).Where(t => t.IsEnum).Select(t => t.FullName).ToList();

        await Assert.That(elsewhere).IsEmpty();
    }
}
