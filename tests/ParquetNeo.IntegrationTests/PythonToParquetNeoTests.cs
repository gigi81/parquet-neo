using System.Text;
using System.Text.Json;
using ParquetNeo.Data;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;

namespace ParquetNeo.IntegrationTests;

/// <summary>Files that pyarrow wrote (<c>python/generate.py</c>) are read by ParquetNeo, and what it reads is what pyarrow had.</summary>
public class PythonToParquetNeoTests
{
    public static IEnumerable<string> FlatCases() => PythonEnvironment.Enabled ? PythonEnvironment.Files.Names("flat") : ["off"];

    public static IEnumerable<string> NestedCases() => PythonEnvironment.Enabled ? PythonEnvironment.Files.Names("nested") : ["off"];

    [Test]
    [MethodDataSource(nameof(FlatCases))]
    public async Task TheColumnsOfAFlatFile_HoldTheValuesPythonWrote(string name)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var entry = PythonEnvironment.Files.Case(name);
        string path = PythonEnvironment.Files.PathOf(name);
        var columns = entry.GetProperty("columns");
        string[] order = [.. entry.GetProperty("column_order").EnumerateArray().Select(e => e.GetString()!)];

        using var schemaReader = ParquetFileReader.Open(File.OpenRead(path), Readers.Options);
        using var reader = ParquetDataReader.Open(File.OpenRead(path), new ParquetDataReaderOptions { Reader = Readers.Options });
        var nodes = order.Select(column => schemaReader.Schema.FindColumn([column])!).ToArray();
        var mismatches = new List<string>();
        int rows = 0;
        int expectedRows = columns.GetProperty(order[0]).GetArrayLength();

        await Assert.That(reader.FieldCount).IsEqualTo(order.Length);
        for (int c = 0; c < order.Length; c++)
        {
            await Assert.That(reader.GetName(c)).IsEqualTo(order[c]);
        }

        while (reader.Read())
        {
            for (int c = 0; c < order.Length && mismatches.Count < 10; c++)
            {
                object? value = reader.IsDBNull(c) ? null : reader.GetValue(c);
                if (Values.Compare(columns.GetProperty(order[c])[rows], value, nodes[c]) is { } difference)
                {
                    mismatches.Add($"{order[c]}[{rows}]: {difference}");
                }
            }

            rows++;
        }

        await Assert.That(rows).IsEqualTo(expectedRows);
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(FlatCases))]
    public async Task TheRowGroupsAndStatisticsOfAFile_AreThoseOfPython(string name)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var entry = PythonEnvironment.Files.Case(name);
        var metadata = entry.GetProperty("metadata");
        using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf(name)), Readers.Options);
        var groups = metadata.GetProperty("row_groups");
        var mismatches = new List<string>();

        await Assert.That(file.RowCount).IsEqualTo(metadata.GetProperty("num_rows").GetInt64());
        await Assert.That(file.RowGroupCount).IsEqualTo(metadata.GetProperty("num_row_groups").GetInt32());
        for (int g = 0; g < file.RowGroupCount; g++)
        {
            var group = file.OpenRowGroup(g);
            await Assert.That(group.RowCount).IsEqualTo(groups[g].GetProperty("num_rows").GetInt64());
            for (int c = 0; c < file.Schema.Columns.Count; c++)
            {
                CompareStatistics(groups[g].GetProperty("columns")[c], group.GetStatistics(c), file.Schema.Columns[c], $"group {g}, column {file.Schema.Columns[c].Name}", mismatches);
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(NestedCases))]
    public async Task TheRowsOfANestedFile_AreThoseOfPython(string name)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var entry = PythonEnvironment.Files.Case(name);
        string path = PythonEnvironment.Files.PathOf(name);
        var columns = entry.GetProperty("columns");

        JsonElement rows = entry.GetProperty("shape").GetString() switch
        {
            "lists" => Read<ListsRow>(path),
            "struct" => Read<StructRow>(path),
            "map" => Read<MapRow>(path),
            "list_struct" => Read<ListStructRow>(path),
            var other => throw new InvalidOperationException($"No class for the shape {other}."),
        };

        int expectedRows = columns.EnumerateObject().First().Value.GetArrayLength();
        var mismatches = new List<string>();
        await Assert.That(rows.GetArrayLength()).IsEqualTo(expectedRows);
        for (int i = 0; i < expectedRows && mismatches.Count < 10; i++)
        {
            foreach (var column in columns.EnumerateObject())
            {
                if (Values.Compare(column.Value[i], rows[i].GetProperty(column.Name), $"{column.Name}[{i}]") is { } difference)
                {
                    mismatches.Add(difference);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [Arguments("bloom_filters")]
    [Arguments("duckdb_bloom_filters")]
    public async Task ABloomFilterOfAnotherWriter_RulesOutTheRowGroupsThatCannotHoldAValue(string name)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var entry = PythonEnvironment.Files.Case(name);
        using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf(name)), Readers.Options);
        var ids = entry.GetProperty("columns").GetProperty("Id").EnumerateArray().Select(e => e.GetInt64()).ToArray();
        var groupSizes = entry.GetProperty("metadata").GetProperty("row_groups").EnumerateArray().Select(g => g.GetProperty("num_rows").GetInt32()).ToArray();
        var firstRows = groupSizes.Select((_, g) => groupSizes.Take(g).Sum()).ToArray();

        // The file has the filters: without them there is nothing to test, and the filters could not rule out anything.
        foreach (var group in entry.GetProperty("metadata").GetProperty("row_groups").EnumerateArray())
        {
            foreach (var column in group.GetProperty("columns").EnumerateArray().Where(c => c.GetProperty("path").GetString() is "Id" or "Name"))
            {
                await Assert.That(column.GetProperty("has_bloom_filter").GetBoolean()).IsTrue().Because($"{name}, {column.GetProperty("path").GetString()}");
            }
        }

        // A value the file holds is never ruled out of the row group that holds it.
        int missed = 0;
        for (int g = 0; g < groupSizes.Length; g++)
        {
            for (int i = firstRows[g]; i < firstRows[g] + groupSizes[g]; i += 97)
            {
                var matching = file.GetMatchingRowGroups(ParquetFilter.Column("Id").Equal(ids[i]));
                var byName = file.GetMatchingRowGroups(ParquetFilter.Column("Name").Equal("name " + ids[i]));
                if (!matching.Contains(g) || !byName.Contains(g))
                {
                    missed++;
                }
            }
        }

        // The odd numbers are between the smallest and the largest of every row group, which the statistics cannot rule out: the filters can.
        int kept = 0;
        int asked = 0;
        for (long odd = 1; odd < 9999; odd += 51)
        {
            kept += file.GetMatchingRowGroups(ParquetFilter.Column("Id").Equal(odd)).Count;
            kept += file.GetMatchingRowGroups(ParquetFilter.Column("Name").Equal("name " + odd)).Count;
            asked += 2;
        }

        await Assert.That(missed).IsEqualTo(0);
        await Assert.That(kept).IsLessThan(asked * file.RowGroupCount / 10);
    }

    [Test]
    public async Task APageIndexOfPython_SelectsTheRowsOfAFilter()
    {
        PythonEnvironment.SkipUnlessEnabled();
        using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf("page_index")), Readers.Options);
        var group = file.OpenRowGroup(0);

        var selection = group.SelectRows(ParquetFilter.Column("Id").Between(100L, 150L), [0, 1]);

        await Assert.That(selection).IsNotNull();
        await Assert.That(selection!.RowCount).IsLessThan(group.RowCount / 4);
        for (long row = 100; row <= 150; row++)
        {
            await Assert.That(selection.Ranges.Any(range => range.Start <= row && row < range.End)).IsTrue().Because($"row {row}");
        }

        using var column = group.OpenColumn(0, selection);
        var ids = new long[selection.RowCount];
        var levels = new int[selection.RowCount]; // pyarrow makes every column optional
        await Assert.That(column.ReadBatch(ids, levels).Values).IsEqualTo((int)selection.RowCount);
        await Assert.That(Enumerable.Range(100, 51).All(id => ids.Contains(id))).IsTrue();
    }

    [Test]
    public async Task TheKeyValueMetadataAndTheWriterName_AreThoseOfPython()
    {
        PythonEnvironment.SkipUnlessEnabled();
        using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf("key_value_metadata")), Readers.Options);

        var pairs = file.Metadata.KeyValueMetadata!.ToDictionary(p => p.Key, p => p.Value);

        await Assert.That(pairs["owner"]).IsEqualTo("tests");
        await Assert.That(pairs["note"]).IsEqualTo("héllo wörld");
        await Assert.That(file.Metadata.CreatedBy).StartsWith("parquet-cpp-arrow");
    }

    [Test]
    public async Task TheKeyValueMetadataFieldIdsAndWriterName_AreThoseOfDuckDb()
    {
        PythonEnvironment.SkipUnlessEnabled();
        using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf("duckdb_kv_and_field_ids")), Readers.Options);

        var pairs = file.Metadata.KeyValueMetadata!.ToDictionary(p => p.Key, p => p.Value);

        await Assert.That(pairs["owner"]).IsEqualTo("tests");
        await Assert.That(pairs["note"]).IsEqualTo("hello");
        await Assert.That(file.Metadata.CreatedBy).StartsWith("DuckDB");
        await Assert.That(file.Schema.FindColumn(["Id"])!.FieldId).IsEqualTo(5);
        await Assert.That(file.Schema.FindColumn(["Name"])!.FieldId).IsEqualTo(9);
    }

    [Test]
    public async Task TheEncodingsOfDuckDb_AreReadInDataPagesOfBothVersions()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var expectations = new (string Case, ParquetEncoding Encoding, string Column)[]
        {
            ("duckdb_basic_snappy_v1", ParquetEncoding.PlainDictionary, "I8"),
            ("duckdb_basic_snappy_v2", ParquetEncoding.RleDictionary, "I8"),
            ("duckdb_basic_snappy_v2", ParquetEncoding.DeltaBinaryPacked, "Id"),
            ("duckdb_basic_zstd_v2", ParquetEncoding.DeltaLengthByteArray, "S"),
        };

        foreach (var (name, encoding, column) in expectations)
        {
            using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf(name)), Readers.Options);
            var chunk = file.OpenRowGroup(0).Metadata.Columns[file.Schema.FindColumn([column])!.ColumnIndex].MetaData!;

            await Assert.That(chunk.Encodings).Contains(encoding).Because($"{name}, {column}");
        }
    }

    [Test]
    public async Task TheCodecsAndEncodingsOfThePages_AreTheOnesPythonChose()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var expectations = new (string Case, CompressionCodec Codec, ParquetEncoding Encoding, string Column)[]
        {
            ("basic_snappy_dict_v1", CompressionCodec.Snappy, ParquetEncoding.RleDictionary, "S"),
            ("basic_zstd_plain_v2", CompressionCodec.Zstd, ParquetEncoding.Plain, "S"),
            ("basic_lz4_raw_plain_v1", CompressionCodec.Lz4Raw, ParquetEncoding.Plain, "I32"),
            ("basic_gzip_plain_v2", CompressionCodec.Gzip, ParquetEncoding.Plain, "F64"),
            ("encoding_delta_binary_v2", CompressionCodec.Snappy, ParquetEncoding.DeltaBinaryPacked, "I64"),
            ("encoding_delta_length", CompressionCodec.Snappy, ParquetEncoding.DeltaLengthByteArray, "S"),
            ("encoding_delta_byte_array", CompressionCodec.Snappy, ParquetEncoding.DeltaByteArray, "B"),
            ("encoding_byte_stream_split", CompressionCodec.Zstd, ParquetEncoding.ByteStreamSplit, "F32"),
        };

        foreach (var (name, codec, encoding, column) in expectations)
        {
            using var file = ParquetFileReader.Open(File.OpenRead(PythonEnvironment.Files.PathOf(name)), Readers.Options);
            var chunk = file.OpenRowGroup(0).Metadata.Columns[file.Schema.FindColumn([column])!.ColumnIndex].MetaData!;

            await Assert.That(chunk.Codec).IsEqualTo(codec).Because(name);
            await Assert.That(chunk.Encodings).Contains(encoding).Because(name);
        }
    }

    private static JsonElement Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.SerializeToElement(ParquetSerializer.Deserialize<T>(stream, new ParquetDeserializerOptions { Reader = Readers.Options }));
    }

    private static void CompareStatistics(JsonElement python, Statistics.ColumnStatistics? statistics, PrimitiveNode column, string where, List<string> mismatches)
    {
        if (python.GetProperty("statistics").ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var expected = python.GetProperty("statistics");
        if (statistics is null)
        {
            mismatches.Add($"{where}: no statistics");
            return;
        }

        if (expected.GetProperty("null_count").ValueKind == JsonValueKind.Number && expected.GetProperty("null_count").GetInt64() != statistics.NullCount)
        {
            mismatches.Add($"{where}: null count {statistics.NullCount}, expected {expected.GetProperty("null_count").GetInt64()}");
        }

        foreach (var (which, found) in new[] { ("min", statistics.Min), ("max", statistics.Max) })
        {
            if (expected.GetProperty(which).ValueKind != JsonValueKind.Null && Values.Compare(expected.GetProperty(which), AsValue(found, column), column) is { } difference)
            {
                mismatches.Add($"{where}, {which}: {difference}");
            }
        }
    }

    /// <summary>A bound as the value of the column: the unsigned integers are stored as signed ones, and text as bytes.</summary>
    private static object? AsValue(object? bound, PrimitiveNode column) => bound switch
    {
        int number when column.LogicalType is { Kind: LogicalTypeKind.Integer, IsSigned: false } => (uint)number,
        long number when column.LogicalType is { Kind: LogicalTypeKind.Integer, IsSigned: false } => (ulong)number,
        byte[] bytes when column.LogicalType?.Kind is LogicalTypeKind.String => Encoding.UTF8.GetString(bytes),
        _ => bound,
    };
}
