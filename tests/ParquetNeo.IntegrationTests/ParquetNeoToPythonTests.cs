using System.Data;
using System.Text;
using System.Text.Json;
using ParquetNeo.Data;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;

namespace ParquetNeo.IntegrationTests;

/// <summary>Files that ParquetNeo writes are read by pyarrow (<c>python/dump.py</c>, which also checks the checksums of the pages), and what it reads is what was written.</summary>
public class ParquetNeoToPythonTests
{
    private const int Rows = 3000;
    private const int RowGroupSize = 1000;

    /// <summary>Every codec, with and without a dictionary, with both versions of the data pages; some with a page index and some with checksums.</summary>
    public static IEnumerable<(CompressionCodec Codec, bool Dictionary, DataPageVersion Version, bool PageIndex, bool Checksums)> Options()
    {
        int index = 0;
        foreach (var codec in new[] { CompressionCodec.Uncompressed, CompressionCodec.Snappy, CompressionCodec.Gzip, CompressionCodec.Zstd, CompressionCodec.Lz4Raw })
        {
            foreach (bool dictionary in new[] { true, false })
            {
                foreach (var version in new[] { DataPageVersion.V1, DataPageVersion.V2 })
                {
                    yield return (codec, dictionary, version, index % 3 == 0, index % 2 == 0);
                    index++;
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Options))]
    public async Task EveryCodecAndEveryPageLayout_IsReadByPython(CompressionCodec codec, bool dictionary, DataPageVersion version, bool pageIndex, bool checksums)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = BasicRow.Many(Rows);
        var options = new ParquetWriterOptions
        {
            Compression = codec,
            DictionaryEnabled = dictionary,
            DataPageVersion = version,
            WritePageIndex = pageIndex,
            WritePageChecksums = checksums,
            DataPageSize = 8192,
        };

        var dump = PythonEnvironment.Dump(Checks.Write(rows, $"options-{codec}", options, RowGroupSize));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        await Assert.That(dump.GetProperty("metadata").GetProperty("num_row_groups").GetInt32()).IsEqualTo(Rows / RowGroupSize);
        foreach (var chunk in Checks.ColumnChunks(dump))
        {
            string column = chunk.GetProperty("path").GetString()!;
            await Assert.That(chunk.GetProperty("compression").GetString()).IsEqualTo(Checks.PythonName(codec)).Because(column);
            await Assert.That(chunk.GetProperty("has_offset_index").GetBoolean()).IsEqualTo(pageIndex).Because(column);
            await Assert.That(chunk.GetProperty("has_column_index").GetBoolean()).IsEqualTo(pageIndex).Because(column);
            await Assert.That(chunk.GetProperty("has_dictionary_page").GetBoolean()).IsEqualTo(dictionary && column != "B").Because(column); // booleans are never in a dictionary
        }
    }

    [Test]
    public async Task TheStatisticsOfEveryColumn_AreTheMinimumMaximumAndNullCountOfItsValues()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = BasicRow.Many(Rows);

        var dump = PythonEnvironment.Dump(Checks.Write(rows, "statistics", new ParquetWriterOptions { Compression = CompressionCodec.Snappy }, RowGroupSize));

        var nodes = ParquetSerializer.GetSchema<BasicRow>().Columns;
        var differences = new List<string>();
        for (int group = 0; group < Rows / RowGroupSize; group++)
        {
            var part = rows.Skip(group * RowGroupSize).Take(RowGroupSize).ToArray();
            foreach (var node in nodes)
            {
                var statistics = Checks.ColumnChunk(dump, group, node.Name).GetProperty("statistics");
                var values = part.Select(r => typeof(BasicRow).GetProperty(node.Name)!.GetValue(r)).ToArray();
                if (CompareStatistics(statistics, values, node) is { } difference)
                {
                    differences.Add($"group {group}, {node.Name}: {difference}");
                }
            }
        }

        await Assert.That(differences).IsEmpty();
    }

    [Test]
    public async Task TheLogicalTypes_AreReadByPythonWithTheirUnits()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = LogicalRow.Many(1200);

        var dump = PythonEnvironment.Dump(Checks.Write(rows, "logical", new ParquetWriterOptions { Compression = CompressionCodec.Zstd }, 500));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        var types = dump.GetProperty("schema").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetString()!);
        await Assert.That(types["Date"]).IsEqualTo("date32[day]");
        await Assert.That(types["TimeMillis"]).IsEqualTo("time32[ms]");
        await Assert.That(types["TimeMicros"]).IsEqualTo("time64[us]");
        await Assert.That(types["TsMillis"]).IsEqualTo("timestamp[ms]");
        await Assert.That(types["TsMicrosUtc"]).IsEqualTo("timestamp[us, tz=UTC]");
        await Assert.That(types["TsNanosUtc"]).IsEqualTo("timestamp[ns, tz=UTC]");
        await Assert.That(types["Dec9"]).IsEqualTo("decimal128(9, 2)");
        await Assert.That(types["Dec18"]).IsEqualTo("decimal128(18, 4)");
        await Assert.That(types["Dec30"]).IsEqualTo("decimal128(30, 6)");
        await Assert.That(types["Half"]).IsEqualTo("halffloat");
        await Assert.That(types["Guid"]).Contains("uuid"); // the bytes are the RFC 4122 ones, in the order of the text of the Guid: the comparison above checks that
    }

    public static IEnumerable<(string Shape, CompressionCodec Codec, DataPageVersion Version)> NestedOptions() =>
        from shape in new[] { "lists", "structs", "maps", "listsOfStructs" }
        from codec in new[] { CompressionCodec.Uncompressed, CompressionCodec.Snappy, CompressionCodec.Zstd }
        from version in new[] { DataPageVersion.V1, DataPageVersion.V2 }
        select (shape, codec, version);

    [Test]
    [MethodDataSource(nameof(NestedOptions))]
    public async Task NestedRows_AreReadByPython(string shape, CompressionCodec codec, DataPageVersion version)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var options = new ParquetWriterOptions { Compression = codec, DataPageVersion = version, DataPageSize = 512 };

        var differences = shape switch
        {
            "lists" => Nested(NestedRows.Lists(400), options),
            "structs" => Nested(NestedRows.Structs(400), options),
            "maps" => Nested(NestedRows.Maps(400), options),
            _ => Nested(NestedRows.ListsOfStructs(400), options),
        };

        await Assert.That(differences).IsEmpty();
    }

    public static IEnumerable<(ParquetEncoding Encoding, string Column)> Encodings() =>
    [
        (ParquetEncoding.DeltaBinaryPacked, "I32"),
        (ParquetEncoding.DeltaBinaryPacked, "Id"),
        (ParquetEncoding.DeltaLengthByteArray, "S"),
        (ParquetEncoding.DeltaLengthByteArray, "Bin"),
        (ParquetEncoding.DeltaByteArray, "S"),
        (ParquetEncoding.ByteStreamSplit, "F32"),
        (ParquetEncoding.ByteStreamSplit, "F64"),
        (ParquetEncoding.Plain, "S"),
    ];

    [Test]
    [MethodDataSource(nameof(Encodings))]
    public async Task TheEncodingOfAColumn_IsTheOneAskedFor_AndPythonDecodesIt(ParquetEncoding encoding, string column)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = BasicRow.Many(Rows);
        var options = new ParquetWriterOptions { DictionaryEnabled = false, ColumnEncodings = new Dictionary<string, ParquetEncoding> { [column] = encoding } };

        var dump = PythonEnvironment.Dump(Checks.Write(rows, "encoding", options, RowGroupSize));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        var encodings = Checks.ColumnChunk(dump, 0, column).GetProperty("encodings").EnumerateArray().Select(e => e.GetString()).ToArray();
        await Assert.That(encodings).Contains(Checks.PythonName(encoding));
    }

    [Test]
    public async Task TheBloomFiltersAndTheKeyValueMetadata_AreThereForPython()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = BasicRow.Many(Rows);
        var options = new ParquetWriterOptions
        {
            ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["Id"] = new(), ["S"] = new() },
            KeyValueMetadata = new Dictionary<string, string> { ["owner"] = "tests", ["note"] = "héllo wörld 😀" },
            CreatedBy = "ParquetNeo integration tests",
        };

        var dump = PythonEnvironment.Dump(Checks.Write(rows, "bloom", options, RowGroupSize));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        foreach (var chunk in Checks.ColumnChunks(dump))
        {
            string column = chunk.GetProperty("path").GetString()!;
            await Assert.That(chunk.GetProperty("has_bloom_filter").GetBoolean()).IsEqualTo(column is "Id" or "S").Because(column);
        }

        var metadata = dump.GetProperty("metadata");
        await Assert.That(metadata.GetProperty("key_value_metadata").GetProperty("owner").GetString()).IsEqualTo("tests");
        await Assert.That(metadata.GetProperty("key_value_metadata").GetProperty("note").GetString()).IsEqualTo("héllo wörld 😀");
        await Assert.That(metadata.GetProperty("created_by").GetString()).IsEqualTo("ParquetNeo integration tests");
    }

    [Test]
    public async Task ColumnsWrittenInParallel_AndAsynchronously_AreReadByPython()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("Name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
            new PrimitiveNode("Score", PhysicalType.Double, FieldRepetitionType.Required),
        ]);
        long[] ids = [.. Enumerable.Range(0, 5000).Select(i => (long)i * 3)];
        string[] names = [.. ids.Select(i => $"name {i} é")];
        double[] scores = [.. ids.Select(i => i / 7d)];
        Action<ColumnWriter>[] actions = [c => c.WriteBatch(ids), c => c.WriteStrings(names), c => c.WriteBatch(scores)];
        var options = Readers.Writer(new ParquetWriterOptions { Compression = CompressionCodec.Zstd, DataPageSize = 4096, WritePageIndex = true });

        string parallel = PythonEnvironment.NewFile("parallel");
        using (var stream = File.Create(parallel))
        using (var writer = ParquetFileWriter.Create(stream, schema, options))
        using (var group = writer.AppendRowGroup())
        {
            group.WriteColumns(actions, maxDegreeOfParallelism: 3);
        }

        string asynchronous = PythonEnvironment.NewFile("async");
        await using (var stream = File.Create(asynchronous))
        await using (var writer = await ParquetFileWriter.CreateAsync(stream, schema, options))
        await using (var group = writer.AppendRowGroup())
        {
            await group.WriteColumnsAsync(actions);
        }

        foreach (string path in new[] { parallel, asynchronous })
        {
            var columns = PythonEnvironment.Dump(path).GetProperty("columns");
            await Assert.That(columns.GetProperty("Id").EnumerateArray().Select(e => e.GetInt64()).SequenceEqual(ids)).IsTrue().Because(path);
            await Assert.That(columns.GetProperty("Name").EnumerateArray().Select(e => e.GetString()).SequenceEqual(names)).IsTrue().Because(path);
            await Assert.That(columns.GetProperty("Score").EnumerateArray().Select(e => e.GetDouble()).SequenceEqual(scores)).IsTrue().Because(path);
        }
    }

    [Test]
    public async Task RowsSerializedAsynchronouslyFromAnAsyncSequence_AreReadByPython()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = BasicRow.Many(2500);
        string path = PythonEnvironment.NewFile("serialize-async");

        await using (var stream = File.Create(path))
        {
            await ParquetSerializer.SerializeAsync(ToAsync(rows), stream, new ParquetSerializerOptions { RowGroupSize = 700, Writer = Readers.Writer(new ParquetWriterOptions { Compression = CompressionCodec.Snappy }) });
        }

        var dump = PythonEnvironment.Dump(path);
        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        await Assert.That(dump.GetProperty("metadata").GetProperty("num_row_groups").GetInt32()).IsEqualTo(4);
    }

    [Test]
    public async Task ARowsOfADataTable_WrittenThroughAdoNet_AreReadByPython()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Big", typeof(long));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Flag", typeof(bool));
        table.Columns.Add("Ratio", typeof(double));
        table.Columns.Add("Blob", typeof(byte[]));
        table.Columns.Add("When", typeof(DateTime));
        for (int i = 0; i < 1500; i++)
        {
            table.Rows.Add(i, (long)i * 1_000_000_007, i % 4 == 0 ? DBNull.Value : $"name {i}", i % 2 == 0, i / 3.0, i % 5 == 0 ? DBNull.Value : new[] { (byte)i, (byte)(i >> 8) }, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
        }

        string path = PythonEnvironment.NewFile("adonet");
        using (var stream = File.Create(path))
        using (var reader = table.CreateDataReader())
        {
            ParquetDataWriter.Write(reader, stream, new ParquetDataWriterOptions { Writer = Readers.Writer(new ParquetWriterOptions { Compression = CompressionCodec.Gzip }) });
        }

        var dump = PythonEnvironment.Dump(path);
        using var written = ParquetFileReader.Open(File.OpenRead(path), Readers.Options);
        var columns = dump.GetProperty("columns");
        var differences = new List<string>();
        foreach (DataColumn column in table.Columns)
        {
            var node = written.Schema.FindColumn([column.ColumnName])!;
            for (int i = 0; i < table.Rows.Count && differences.Count < 10; i++)
            {
                if (Values.Compare(columns.GetProperty(column.ColumnName)[i], table.Rows[i][column], node) is { } difference)
                {
                    differences.Add($"{column.ColumnName}[{i}]: {difference}");
                }
            }
        }

        await Assert.That(differences).IsEmpty();
    }

    public static IEnumerable<string> EdgeCases() => ["empty", "single", "nulls", "special-floats", "long-values", "unicode", "many-groups", "large", "dictionary-fallback"];

    [Test]
    [MethodDataSource(nameof(EdgeCases))]
    public async Task EdgeCases_AreReadByPython(string name)
    {
        PythonEnvironment.SkipUnlessEnabled();
        var options = new ParquetWriterOptions { Compression = CompressionCodec.Zstd };
        BasicRow[] rows;
        int rowGroupSize = 1000;
        switch (name)
        {
            case "empty":
                rows = [];
                break;
            case "single":
                rows = BasicRow.Many(1);
                break;
            case "nulls":
                rows = [.. BasicRow.Many(500).Select(r => new BasicRow { Id = r.Id })];
                break;
            case "special-floats":
                rows = [.. BasicRow.Many(100).Select(r => { r.F32 = r.Id switch { 0 => float.NaN, 1 => float.PositiveInfinity, 2 => float.NegativeInfinity, 3 => -0f, 4 => float.Epsilon, 5 => float.MaxValue, _ => r.F32 }; r.F64 = r.Id switch { 0 => double.NaN, 1 => double.PositiveInfinity, 2 => double.NegativeInfinity, 3 => -0d, 4 => double.Epsilon, 5 => double.MaxValue, _ => r.F64 }; return r; })];
                break;
            case "long-values":
                rows = [.. BasicRow.Many(20).Select(r => { r.S = new string('é', 300_000); r.Bin = new byte[1_000_000]; return r; })];
                options = options with { DataPageSize = 1_000_000 };
                break;
            case "unicode":
                rows = [.. BasicRow.Many(300).Select(r => { r.S = new[] { "", "ascii", "é ü ñ", "日本語のテキスト", "😀🎉 emoji", "a\u0000b", "​﻿" }[r.Id % 7]; return r; })];
                break;
            case "many-groups":
                rows = BasicRow.Many(3000);
                rowGroupSize = 7;
                break;
            case "large":
                rows = BasicRow.Many(150_000);
                rowGroupSize = 50_000;
                break;
            default:
                rows = [.. BasicRow.Many(20_000).Select(r => { r.S = r.Id > 5000 ? $"value {r.Id * 7919 % 20011}" : $"repeat {r.Id % 4}"; return r; })];
                options = options with { DictionaryPageSizeLimit = 2048, DataPageSize = 4096 };
                break;
        }

        var dump = PythonEnvironment.Dump(Checks.Write(rows, "edge-" + name, options, rowGroupSize));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
        await Assert.That(dump.GetProperty("metadata").GetProperty("num_rows").GetInt64()).IsEqualTo(rows.Length);
    }

    private static List<string> Nested<T>(T[] rows, ParquetWriterOptions options) =>
        Checks.CompareNested(PythonEnvironment.Dump(Checks.Write(rows, "nested-" + typeof(T).Name, options, 128)), rows);

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    /// <summary>Compares the statistics of a column chunk that Python read with those of its values: numbers exactly, text and binary as bounds (they are cut short).</summary>
    private static string? CompareStatistics(JsonElement statistics, object?[] values, PrimitiveNode node)
    {
        if (statistics.ValueKind != JsonValueKind.Object || !statistics.GetProperty("has_min_max").GetBoolean())
        {
            return "no statistics";
        }

        var present = values.Where(v => v is not null && !(v is float f && float.IsNaN(f)) && !(v is double d && double.IsNaN(d))).ToArray();
        long nulls = values.Count(v => v is null);
        if (statistics.GetProperty("null_count").GetInt64() != nulls)
        {
            return $"null count {statistics.GetProperty("null_count").GetInt64()}, expected {nulls}";
        }

        if (present.Length == 0)
        {
            return null;
        }

        if (present[0] is string or byte[])
        {
            return BoundsOf(statistics, present, node.LogicalType?.Kind == LogicalTypeKind.String);
        }

        var ordered = present.OrderBy(v => v, Comparer<object?>.Create((a, b) => ((IComparable)a!).CompareTo(b))).ToArray();
        var (min, max) = (ordered[0], ordered[^1]);
        if (present[0] is bool)
        {
            min = present.Any(v => !(bool)v!) ? false : true;
            max = present.Any(v => (bool)v!) ? true : false;
        }

        return Values.Compare(statistics.GetProperty("min"), min, node) is { } low ? "minimum " + low
            : Values.Compare(statistics.GetProperty("max"), max, node) is { } high ? "maximum " + high : null;
    }

    private static string? BoundsOf(JsonElement statistics, object?[] present, bool text)
    {
        byte[] Bytes(object? v) => v is string s ? Encoding.UTF8.GetBytes(s) : (byte[])v!;
        byte[] FromJson(JsonElement e) => text ? Encoding.UTF8.GetBytes(e.GetString()!) : Convert.FromHexString(e.GetString()!);

        var all = present.Select(Bytes).ToArray();
        byte[] smallest = all.Aggregate((a, b) => a.AsSpan().SequenceCompareTo(b) <= 0 ? a : b);
        byte[] largest = all.Aggregate((a, b) => a.AsSpan().SequenceCompareTo(b) >= 0 ? a : b);
        byte[] min = FromJson(statistics.GetProperty("min"));
        byte[] max = FromJson(statistics.GetProperty("max"));
        if (min.AsSpan().SequenceCompareTo(smallest) > 0)
        {
            return "the minimum is above the smallest value";
        }

        return max.AsSpan().SequenceCompareTo(largest) < 0 ? "the maximum is below the largest value" : null;
    }
}
