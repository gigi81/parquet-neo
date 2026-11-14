using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

/// <summary>
/// Pins the exact bytes the writer produces for a small file, so that an accidental change to the on-disk
/// format cannot slip through. The expected file was checked with pyarrow when it was created.
/// </summary>
/// <remarks>
/// To regenerate them after an intended change, run these tests once with the environment variable
/// PARQUETNEO_GOLDEN_OUT set to the fixtures directory; the tests then write the files instead of comparing.
/// Check the new file with an independent reader before committing it.
/// </remarks>
public class GoldenFileTests
{
    private const string PlainFixture = "golden-flat-uncompressed.parquet";
    private const string DictionaryFixture = "golden-flat-dictionary.parquet";

    [Test]
    [Arguments(PlainFixture, false)]
    [Arguments(DictionaryFixture, true)]
    public async Task TheWriterOutput_MatchesTheGoldenFile_ByteForByte(string fixture, bool dictionary)
    {
        var actual = Build(dictionary);

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, fixture), actual);
            return;
        }

        var expected = File.ReadAllBytes(TestBytes.FixturePath(fixture));

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(expected));
    }

    [Test]
    [Arguments(PlainFixture)]
    [Arguments(DictionaryFixture)]
    public async Task TheGoldenFile_HoldsTheExpectedData(string fixture)
    {
        var file = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath(fixture)));
        file.AssertStructure();

        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(new long?[] { 1, 2, 3, 4, 5, 6, 7 });
        await Assert.That(file.ReadStrings(0, 1)).IsEquivalentTo(new[] { "alpha", null, "δelta", "alpha", null, "", "δelta" });
        await Assert.That(file.ReadColumn<double>(0, 2)).IsEquivalentTo(new double?[] { 1.5, null, null, 1.5, 5.0, null, 5.0 });
        await Assert.That(file.ReadColumn<bool>(0, 3)).IsEquivalentTo(new bool?[] { true, false, true, true, false, false, true });
        await Assert.That(file.ReadColumn<int>(0, 4)).IsEquivalentTo(new int?[] { 18000, null, 18000, 18003, null, 18003, 18006 });
        await Assert.That(file.Metadata.CreatedBy).IsEqualTo("ParquetNeo golden");
    }

    [Test]
    public async Task OnlyTheDictionaryGolden_HasDictionaryPages()
    {
        var plain = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath(PlainFixture)));
        var dictionary = TestParquetFile.Parse(File.ReadAllBytes(TestBytes.FixturePath(DictionaryFixture)));

        // The booleans (column 3) have an encoding of their own and stay PLAIN.
        await Assert.That(plain.Metadata.RowGroups[0].Columns.Count(c => c.MetaData!.DictionaryPageOffset is not null)).IsEqualTo(0);
        await Assert.That(dictionary.Metadata.RowGroups[0].Columns.Select(c => c.MetaData!.DictionaryPageOffset is not null))
            .IsEquivalentTo(new[] { true, true, true, false, true });
    }

    private static byte[] Build(bool dictionary)
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
            DictionaryEnabled = dictionary,
        };

        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1L, 2, 3, 4, 5, 6, 7]);
            group.NextColumn().WriteStrings(["alpha", "δelta", "alpha", "", "δelta"], [1, 0, 1, 1, 0, 1, 1]);
            group.NextColumn().WriteBatch([1.5, 1.5, 5.0, 5.0], [1, 0, 0, 1, 1, 0, 1]);
            group.NextColumn().WriteBatch([true, false, true, true, false, false, true]);
            group.NextColumn().WriteBatch([18000, 18000, 18003, 18003, 18006], [1, 0, 1, 1, 0, 1, 1]);
        }

        return stream.ToArray();
    }
}
