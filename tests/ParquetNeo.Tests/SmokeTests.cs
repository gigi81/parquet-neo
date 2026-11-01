namespace ParquetNeo.Tests;

public class SmokeTests
{
    [Test]
    public async Task LibraryAssemblyIsReferencedByTests()
    {
        var assembly = System.Reflection.Assembly.Load("ParquetNeo");

        await Assert.That(assembly.GetName().Name).IsEqualTo("ParquetNeo");
    }
}
