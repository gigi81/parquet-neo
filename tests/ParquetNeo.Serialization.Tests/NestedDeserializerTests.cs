using ParquetNeo.DataModel;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;
using ParquetNeo.Tests.Core.Nested;
using ParquetNeo.Tests;

namespace ParquetNeo.Serialization.Tests;

public class NestedDeserializerTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    private static ParquetDeserializerOptions WithCodecs => new() { Reader = new ParquetReaderOptions { Codecs = TestParquetFile.Registry } };

    private static async Task AssertSame<TRow>(IReadOnlyList<TRow> expected, IReadOnlyList<TRow> actual, string where)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Count).Because(where);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(DeepComparer.Difference(expected[i], actual[i])).IsNull().Because($"{where}, row {i}");
        }
    }

    // ---- our own nested files ----

    [Test]
    [Arguments(200, 1000)]
    [Arguments(200, 64)]
    [Arguments(200, 7)]
    [Arguments(1, 1000)]
    [Arguments(0, 1000)]
    public async Task Documents_RoundTrip_ThroughEveryReadPath(int count, int rowGroupSize)
    {
        var rows = Document.Rows(count);
        var bytes = Serialize(rows, new ParquetSerializerOptions { RowGroupSize = rowGroupSize });
        string where = $"{count} documents in groups of {rowGroupSize}";

        await AssertSame(rows, ParquetSerializer.Deserialize<Document>(new MemoryStream(bytes)), $"{where}, sync");
        await AssertSame(rows, ParquetSerializer.DeserializeAll<Document>(new MemoryStream(bytes)).ToList(), $"{where}, lazy");
        await AssertSame(rows, await ParquetSerializer.DeserializeAsync<Document>(new MemoryStream(bytes)), $"{where}, async");
        var streamed = new List<Document>();
        await foreach (var row in ParquetSerializer.DeserializeAllAsync<Document>(new MemoryStream(bytes)))
        {
            streamed.Add(row);
        }

        await AssertSame(rows, streamed, $"{where}, async stream");
    }

    [Test]
    public async Task ManyDocuments_AreReadAcrossSeveralBatchesOfEntries()
    {
        var rows = Document.Rows(6000);

        var read = ParquetSerializer.Deserialize<Document>(new MemoryStream(Serialize(rows)));

        await AssertSame(rows, read, "6000 documents");
    }

    // ---- files written by pyarrow ----

    [Test]
    [Arguments("pyarrow-nested-v1.parquet")]
    [Arguments("pyarrow-nested-dictionary-v1.parquet")]
    [Arguments("pyarrow-nested-v2-snappy.parquet")]
    [Arguments("pyarrow-nested-v1-smallpages.parquet")]
    [Arguments("golden-nested-plain.parquet")]
    [Arguments("golden-nested-dictionary.parquet")]
    [Arguments("golden-nested-v2.parquet")]
    public async Task TheNestedDatasetFiles_ReadIntoDocuments(string fixture)
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath(fixture));

        var rows = ParquetSerializer.Deserialize<Document>(new MemoryStream(bytes), WithCodecs);
        var asynchronous = await ParquetSerializer.DeserializeAsync<Document>(new MemoryStream(bytes), WithCodecs);

        await AssertSame(Document.Rows(), rows, fixture);
        await AssertSame(Document.Rows(), asynchronous, $"{fixture} async");
    }

    private sealed class LegacyRow
    {
        [ParquetColumn("two_level")]
        public List<int>? TwoLevel { get; set; }

        [ParquetColumn("struct_element")]
        public List<LegacyItem>? StructElement { get; set; }

        [ParquetColumn("tuple")]
        public List<LegacyText>? Tuple { get; set; }

        [ParquetColumn("array")]
        public LegacyText[]? Array { get; set; }

        [ParquetColumn("implicit")]
        public int[] Implicit { get; set; } = [];
    }

    private sealed class LegacyItem
    {
        [ParquetColumn("str")]
        public string Text { get; set; } = "";

        [ParquetColumn("num")]
        public int Number { get; set; }
    }

    private sealed class LegacyText
    {
        [ParquetColumn("str")]
        public string Text { get; set; } = "";
    }

    [Test]
    public async Task TheOlderLayoutsOfLists_AreReadAsLists()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("golden-legacy-lists.parquet"));

        var rows = ParquetSerializer.Deserialize<LegacyRow>(new MemoryStream(bytes), WithCodecs);
        var asynchronous = await ParquetSerializer.DeserializeAsync<LegacyRow>(new MemoryStream(bytes), WithCodecs);

        await Assert.That(rows.Count).IsEqualTo(LegacyListFile.RowCount);
        foreach (var list in new[] { rows, asynchronous })
        {
            for (int i = 0; i < list.Count; i++)
            {
                var row = list[i];
                await Assert.That(row.TwoLevel is null ? "null" : string.Join(',', row.TwoLevel)).IsEqualTo((i % 3) switch { 0 => "null", 1 => "", _ => $"{i},{i + 1}" }).Because($"two_level {i}");
                await Assert.That(row.StructElement is null ? "null" : string.Join(',', row.StructElement.Select(e => $"{e.Text}:{e.Number}")))
                    .IsEqualTo((i % 3) switch { 0 => "null", 1 => "", _ => $"s{i}:{i},t:{-i}" }).Because($"struct_element {i}");
                await Assert.That(row.Tuple is null ? "null" : string.Join(',', row.Tuple.Select(e => e.Text))).IsEqualTo(i % 2 == 0 ? "null" : $"a{i},b").Because($"tuple {i}");
                await Assert.That(row.Array is null ? "null" : string.Join(',', row.Array.Select(e => e.Text))).IsEqualTo(i % 2 == 0 ? "" : $"x{i}").Because($"array {i}");
                await Assert.That(string.Join(',', row.Implicit)).IsEqualTo(string.Join(',', Enumerable.Range(0, i % 3).Select(n => i * 10 + n))).Because($"implicit {i}");
            }
        }
    }
}
