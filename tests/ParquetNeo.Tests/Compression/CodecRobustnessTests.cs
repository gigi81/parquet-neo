using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>Whatever the input, decompression either succeeds or throws <see cref="ParquetNeo.Exceptions.CompressionException"/>: the built-in codecs.</summary>
public class CodecRobustnessTests
{
    private static IEnumerable<(string Name, ICodec Codec)> Real => new[] { "gzip", "brotli" }.Select(BuiltInCodecs.Named);

    // The BCL gzip inflater stops at the end of the deflate stream and never looks at the 8-byte trailer, so losing only the
    // last byte(s) of a gzip member leaves complete data and is accepted.
    [Test]
    public Task TruncatedData_IsRejected_ByGzipExceptForItsTrailer() =>
        CodecConformance.TruncatedData_IsRejected([BuiltInCodecs.Named("gzip")], lastBytesMayBeMissing: true);

    [Test]
    public Task TruncatedData_IsRejected_ByBrotli() => CodecConformance.TruncatedData_IsRejected([BuiltInCodecs.Named("brotli")]);

    [Test]
    public Task WrongDestinationSize_IsRejected_InBothDirections() => CodecConformance.WrongDestinationSize_IsRejected_InBothDirections(Real);

    [Test]
    public Task EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid() => CodecConformance.EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid(Real);

    [Test]
    public Task GarbageInput_OnlyEverFailsWithCompressionException() => CodecConformance.GarbageInput_OnlyEverFailsWithCompressionException(Real);

    [Test]
    public Task CorruptedValidData_OnlyEverFailsWithCompressionException() => CodecConformance.CorruptedValidData_OnlyEverFailsWithCompressionException(Real);
}
