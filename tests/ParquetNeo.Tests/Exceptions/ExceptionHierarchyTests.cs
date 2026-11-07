using ParquetNeo.Compression;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Exceptions;

public class ExceptionHierarchyTests
{
    [Test]
    public async Task EveryLibraryException_DerivesFromParquetException()
    {
        var types = typeof(ParquetException).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "ParquetNeo.Exceptions" && typeof(Exception).IsAssignableFrom(type))
            .ToList();

        await Assert.That(types.Count).IsGreaterThan(5);
        foreach (var type in types.Where(type => type != typeof(ParquetException)))
        {
            await Assert.That(type.IsSubclassOf(typeof(ParquetException))).IsTrue().Because(type.Name);
            await Assert.That(type.IsSealed).IsTrue().Because($"{type.Name} is a leaf");
        }

        await Assert.That(typeof(ParquetException).IsAbstract).IsTrue();
        await Assert.That(typeof(ParquetException).BaseType).IsEqualTo(typeof(Exception));
    }

    [Test]
    public async Task ASpecialisedException_IsNotAFormatException()
    {
        // A malformed file is only one of the things that can go wrong; each kind has its own type.
        var specialised = new Exception[]
        {
            new MetadataException("m"), new InvalidSchemaException("s"), new EncodingException("e"),
            new CompressionException("c"), new CorruptColumnException("x", "d"), new ValueConversionException("v"),
        };

        foreach (var exception in specialised)
        {
            await Assert.That(exception is ParquetFormatException).IsFalse().Because(exception.GetType().Name);
            await Assert.That(exception is ParquetException).IsTrue().Because(exception.GetType().Name);
        }
    }

    [Test]
    public async Task ACorruptColumnException_NamesTheColumn()
    {
        var exception = new CorruptColumnException("price", "its pages hold 3 values but the column chunk declares 4");

        await Assert.That(exception.ColumnName).IsEqualTo("price");
        await Assert.That(exception.Message).IsEqualTo("Column 'price' is corrupt: its pages hold 3 values but the column chunk declares 4.");
    }

    [Test]
    public async Task InvalidCompressedData_KeepsTheCodecsOwnExceptionAsTheInnerException()
    {
        var gzip = new GzipCodec();
        var garbage = new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

        var exception = Assert.Throws<CompressionException>(() => gzip.Decompress(garbage, new byte[100]));

        await Assert.That(exception.InnerException).IsNotNull();
    }

    [Test]
    public async Task TheInnerExceptionIsKept()
    {
        var inner = new InvalidOperationException("cause");

        await Assert.That(new ParquetFormatException("m", inner).InnerException).IsSameReferenceAs(inner);
        await Assert.That(new MetadataException("m", inner).InnerException).IsSameReferenceAs(inner);
        await Assert.That(new EncodingException("m", inner).InnerException).IsSameReferenceAs(inner);
    }
}
