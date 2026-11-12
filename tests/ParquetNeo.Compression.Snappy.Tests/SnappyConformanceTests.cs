using ParquetNeo.Tests.Compression;

namespace ParquetNeo.Compression.Snappy.Tests;

/// <summary>What every codec must do, run for the codecs of this package.</summary>
public class SnappyConformanceTests
{
    private static readonly (string Name, ICodec Codec)[] Codecs = [("snappy", new SnappyCodec())];

    private static (string Name, ICodec Codec) Primary => Codecs[0];

    private static IEnumerable<(string Name, ICodec Codec)> PrimaryOnly => Codecs.Take(1);

    [Test]
    public Task EveryCase_RoundTrips_WithinTheMaximumLength() => CodecConformance.EveryCase_RoundTrips_WithinTheMaximumLength(Codecs);

    [Test]
    public Task CompressibleData_ReallyShrinks() => CodecConformance.CompressibleData_ReallyShrinks(Codecs);

    [Test]
    public Task IncompressibleData_StaysWithinTheMaximumLength() => CodecConformance.IncompressibleData_StaysWithinTheMaximumLength(Codecs);

    [Test]
    public Task EmptyInput_ProducesAValidNonEmptyStream() => CodecConformance.EmptyInput_ProducesAValidNonEmptyStream(Codecs);

    [Test]
    public Task InputsAroundBlockBoundaries_RoundTrip() => CodecConformance.InputsAroundBlockBoundaries_RoundTrip(PrimaryOnly);

    [Test]
    public Task RandomStructuredInputs_RoundTrip() => CodecConformance.RandomStructuredInputs_RoundTrip(Codecs);

    [Test]
    public Task ReportsItsParquetId() => CodecConformance.ReportsItsParquetId(Primary.Codec, CompressionCodec.Snappy);

    [Test]
    public Task Compress_RejectsADestinationThatMightBeTooSmall() => CodecConformance.Compress_RejectsADestinationThatMightBeTooSmall(Primary.Codec);

    [Test]
    public Task IndependentlyCompressedData_DecompressesToTheOriginal() => CodecConformance.IndependentlyCompressedData_DecompressesToTheOriginal(Primary.Codec, "snappy");

    [Test]
    public Task TruncatedData_IsRejected() => CodecConformance.TruncatedData_IsRejected(PrimaryOnly);

    [Test]
    public Task WrongDestinationSize_IsRejected_InBothDirections() => CodecConformance.WrongDestinationSize_IsRejected_InBothDirections(PrimaryOnly);

    [Test]
    public Task EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid() => CodecConformance.EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid(PrimaryOnly);

    [Test]
    public Task GarbageInput_OnlyEverFailsWithCompressionException() => CodecConformance.GarbageInput_OnlyEverFailsWithCompressionException(PrimaryOnly);

    [Test]
    public Task CorruptedValidData_OnlyEverFailsWithCompressionException() => CodecConformance.CorruptedValidData_OnlyEverFailsWithCompressionException(PrimaryOnly);
}
