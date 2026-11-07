using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

/// <summary>
/// Pins the exact bytes the writer produces for a small file, so that an accidental change to the on-disk
/// format cannot slip through. The expected file was checked with pyarrow when it was created.
/// </summary>
/// <remarks>
/// To regenerate it after an intended change, run this test once with the environment variable
/// PARQUETNEO_GOLDEN_OUT set to the fixture's path; the test then writes the file instead of comparing.
/// Check the new file with an independent reader before committing it.
/// </remarks>
public class GoldenFileTests
{
    private const string FixtureName = "golden-flat-uncompressed.parquet";

    [Test]
    public async Task TheWriterOutput_MatchesTheGoldenFile_ByteForByte()
    {
        var actual = Build();

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(regenerateTo, actual);
            return;
        }

        var expected = File.ReadAllBytes(TestBytes.FixturePath(FixtureName));

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(expected));
    }

    [Test]
    public async Task TheGoldenFile_HoldsTheExpectedData()
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath(FixtureName)));
        file.AssertStructure();

        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(new long?[] { 1, 2, 3, 4, 5, 6, 7 });
        await Assert.That(file.ReadStrings(0, 1)).IsEquivalentTo(new[] { "alpha", null, "gamma", "δelta", null, "", "zeta" });
        await Assert.That(file.ReadColumn<double>(0, 2)).IsEquivalentTo(new double?[] { 1.5, null, null, 4.25, 5.0, null, 7.75 });
        await Assert.That(file.ReadColumn<bool>(0, 3)).IsEquivalentTo(new bool?[] { true, false, true, true, false, false, true });
        await Assert.That(file.ReadColumn<int>(0, 4)).IsEquivalentTo(new int?[] { 18000, null, 18002, 18003, null, 18005, 18006 });
        await Assert.That(file.Metadata.CreatedBy).IsEqualTo("ParquetNeo golden");
    }

    private static byte[] Build()
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
            new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Optional),
            new PrimitiveNode("flag", PhysicalType.Boolean, FieldRepetitionType.Required),
            new PrimitiveNode("day", PhysicalType.Int32, FieldRepetitionType.Optional, LogicalType.Date),
        ]);
        var options = new ParquetWriterOptions
        {
            CreatedBy = "ParquetNeo golden",
            KeyValueMetadata = new Dictionary<string, string> { ["purpose"] = "golden" },
        };

        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1L, 2, 3, 4, 5, 6, 7]);
            group.NextColumn().WriteStrings(["alpha", "gamma", "δelta", "", "zeta"], [1, 0, 1, 1, 0, 1, 1]);
            group.NextColumn().WriteBatch([1.5, 4.25, 5.0, 7.75], [1, 0, 0, 1, 1, 0, 1]);
            group.NextColumn().WriteBatch([true, false, true, true, false, false, true]);
            group.NextColumn().WriteBatch([18000, 18002, 18003, 18005, 18006], [1, 0, 1, 1, 0, 1, 1]);
        }

        return stream.ToArray();
    }
}
