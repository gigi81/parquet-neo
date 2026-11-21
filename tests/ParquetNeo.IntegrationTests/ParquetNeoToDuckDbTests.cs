using System.Text;
using System.Text.Json;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;

namespace ParquetNeo.IntegrationTests;

/// <summary>
/// Files that ParquetNeo writes are read by DuckDB, a second implementation of Parquet that is not Arrow's: the data, and the two things pyarrow does not read,
/// the bloom filters (<c>parquet_bloom_probe</c>) and the page index (<c>parquet_page_index</c>).
/// </summary>
public class ParquetNeoToDuckDbTests
{
    private const int Rows = 3000;

    public static IEnumerable<(CompressionCodec Codec, bool Dictionary, DataPageVersion Version, bool PageIndex, bool Checksums)> Options() => ParquetNeoToPythonTests.Options();

    [Test]
    [MethodDataSource(nameof(Options))]
    public async Task EveryCodecAndEveryPageLayout_IsReadByDuckDb(CompressionCodec codec, bool dictionary, DataPageVersion version, bool pageIndex, bool checksums)
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

        var dump = PythonEnvironment.DuckDbDump(Checks.Write(rows, $"duckdb-{codec}", options, 1000));

        await Assert.That(Checks.CompareFlat(dump, rows)).IsEmpty();
    }

    [Test]
    public async Task TheLogicalTypes_AreReadByDuckDb()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var rows = LogicalRow.Many(1200);

        // DuckDB reads a time and a timestamp in milliseconds in microseconds, so those two columns are not compared.
        var dump = PythonEnvironment.DuckDbDump(Checks.Write(rows, "duckdb-logical", new ParquetWriterOptions { Compression = CompressionCodec.Zstd }, 500), "TimeMillis", "TsMillis");

        await Assert.That(Checks.CompareFlat(dump, rows, "TimeMillis", "TsMillis")).IsEmpty();
    }

    public static IEnumerable<(string Shape, CompressionCodec Codec, DataPageVersion Version)> NestedOptions() => ParquetNeoToPythonTests.NestedOptions();

    [Test]
    [MethodDataSource(nameof(NestedOptions))]
    public async Task NestedRows_AreReadByDuckDb(string shape, CompressionCodec codec, DataPageVersion version)
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

    [Test]
    public async Task TheFilesOfTheParallelAndTheAsynchronousWriters_AreReadByDuckDb()
    {
        PythonEnvironment.SkipUnlessEnabled();
        var schema = new ParquetSchemaBuilder().Int64("Id").String("Name").Double("Score").Build();
        long[] ids = [.. Enumerable.Range(0, 5000).Select(i => (long)i * 3)];
        string[] names = [.. ids.Select(i => $"name {i} é")];
        double[] scores = [.. ids.Select(i => i / 7d)];
        Action<ColumnWriter>[] actions = [c => c.WriteBatch(ids), c => c.WriteStrings(names), c => c.WriteBatch(scores)];
        var options = Readers.Writer(new ParquetWriterOptions { Compression = CompressionCodec.Zstd, DataPageSize = 4096, WritePageIndex = true });

        string parallel = PythonEnvironment.NewFile("duckdb-parallel");
        using (var stream = File.Create(parallel))
        using (var writer = ParquetFileWriter.Create(stream, schema, options))
        using (var group = writer.AppendRowGroup())
        {
            group.WriteColumns(actions, maxDegreeOfParallelism: 3);
        }

        string asynchronous = PythonEnvironment.NewFile("duckdb-async");
        await using (var stream = File.Create(asynchronous))
        await using (var writer = await ParquetFileWriter.CreateAsync(stream, schema, options))
        await using (var group = writer.AppendRowGroup())
        {
            await group.WriteColumnsAsync(actions);
        }

        foreach (string path in new[] { parallel, asynchronous })
        {
            var columns = PythonEnvironment.DuckDbDump(path).GetProperty("columns");
            await Assert.That(columns.GetProperty("Id").EnumerateArray().Select(e => e.GetInt64()).SequenceEqual(ids)).IsTrue().Because(path);
            await Assert.That(columns.GetProperty("Name").EnumerateArray().Select(e => e.GetString()).SequenceEqual(names)).IsTrue().Because(path);
            await Assert.That(columns.GetProperty("Score").EnumerateArray().Select(e => e.GetDouble()).SequenceEqual(scores)).IsTrue().Because(path);
        }
    }

    // ---- bloom filters ----

    /// <summary>The columns that get a bloom filter, with the DuckDB type to probe them with.</summary>
    private static readonly (string Column, string Type)[] BloomColumns = [("Id", "BIGINT"), ("I32", "INTEGER"), ("F32", "FLOAT"), ("F64", "DOUBLE"), ("S", "VARCHAR"), ("Bin", "BLOB")];

    [Test]
    public async Task TheBloomFiltersOfParquetNeo_AreReadByDuckDb_AndNeverRuleOutAValueTheRowGroupHolds()
    {
        PythonEnvironment.SkipUnlessEnabled();
        const int groupSize = 500;
        // Distinct values, so that each row group holds its own; the odd numbers of the ranges are in none.
        var rows = BasicRow.Many(4000).Select(r =>
        {
            r.Id = r.Id * 2;
            r.I32 = r.Id % 7 == 0 ? null : (int)(r.Id * 3);
            r.F32 = (float)(r.Id * 0.5);
            r.F64 = r.Id * 0.25;
            r.S = $"value {r.Id}";
            r.Bin = BitConverter.GetBytes(r.Id);
            return r;
        }).ToArray();
        var options = new ParquetWriterOptions
        {
            Compression = CompressionCodec.Snappy,
            ColumnBloomFilters = BloomColumns.ToDictionary(c => c.Column, _ => new BloomFilterSettings()),
        };
        string path = Checks.Write(rows, "duckdb-bloom", options, groupSize);

        var present = new List<(string Column, string Type, object Value, int Group)>();
        for (int g = 0; g < rows.Length / groupSize; g++)
        {
            foreach (var row in rows.Skip((g * groupSize) + 11).Where((_, i) => i % 83 == 0).Take(4))
            {
                present.Add(("Id", "BIGINT", row.Id, g));
                present.Add(("F32", "FLOAT", (double)row.F32!.Value, g));
                present.Add(("F64", "DOUBLE", row.F64!.Value, g));
                present.Add(("S", "VARCHAR", row.S!, g));
                present.Add(("Bin", "BLOB", Convert.ToHexString(row.Bin!).ToLowerInvariant(), g));
                if (row.I32 is { } i32)
                {
                    present.Add(("I32", "INTEGER", i32, g));
                }
            }
        }

        var absent = Enumerable.Range(0, 60).Select(i => 1L + (2 * (i * 131L))).ToArray(); // odd numbers, inside the ranges
        var answer = PythonEnvironment.DuckDbProbe(new
        {
            file = path,
            bloom = BloomColumns.Select(c => new
            {
                column = c.Column,
                type = c.Type,
                values = present.Where(p => p.Column == c.Column).Select(p => p.Value)
                    .Concat(absent.Select(a => c.Type switch
                    {
                        "VARCHAR" => (object)$"value {a}",
                        "BLOB" => Convert.ToHexString(BitConverter.GetBytes(a)).ToLowerInvariant(),
                        "FLOAT" => a * 0.5,
                        "DOUBLE" => a * 0.25,
                        "INTEGER" => a * 3 % int.MaxValue,
                        _ => a,
                    }))
                    .Distinct().ToArray(),
            }).ToArray(),
        });

        // DuckDB reads a filter of every column and of every row group, or the answer would be an error, or all false.
        int missed = 0;
        int ruledOut = 0;
        int asked = 0;
        foreach (var probe in answer.GetProperty("bloom").EnumerateArray())
        {
            string column = probe.GetProperty("column").GetString()!;
            var results = probe.GetProperty("results");
            foreach (var (_, _, value, group) in present.Where(p => p.Column == column))
            {
                var excludes = results.GetProperty(ToKey(value)).EnumerateArray().Select(e => e.GetBoolean()).ToArray();
                await Assert.That(excludes.Length).IsEqualTo(rows.Length / groupSize);
                if (excludes[group])
                {
                    missed++;
                }
            }

            foreach (var result in results.EnumerateObject().Where(r => !present.Any(p => p.Column == column && ToKey(p.Value) == r.Name)))
            {
                var excludes = result.Value.EnumerateArray().Select(e => e.GetBoolean()).ToArray();
                ruledOut += excludes.Count(e => e);
                asked += excludes.Length;
            }
        }

        await Assert.That(missed).IsEqualTo(0);
        await Assert.That(asked).IsGreaterThan(0);
        await Assert.That(ruledOut).IsGreaterThan(asked * 9 / 10); // 1% of false positives is asked for: nine tenths ruled out is far from it
    }

    // ---- the page index ----

    [Test]
    public async Task ThePageIndexOfParquetNeo_IsReadByDuckDb_AndIsTheBoundsOfTheValuesOfEachPage()
    {
        PythonEnvironment.SkipUnlessEnabled();
        const int groupSize = 1500;
        var rows = BasicRow.Many(4500);
        var options = new ParquetWriterOptions { Compression = CompressionCodec.Zstd, WritePageIndex = true, DataPageSize = 2048 };
        string path = Checks.Write(rows, "duckdb-page-index", options, groupSize);

        var pages = PythonEnvironment.DuckDbProbe(new { file = path, page_index = true }).GetProperty("page_index").EnumerateArray().ToArray();

        var columns = new Dictionary<string, Func<BasicRow, object?>>
        {
            ["Id"] = r => r.Id,
            ["I32"] = r => r.I32,
            ["F64"] = r => r.F64,
            ["S"] = r => r.S,
            ["U16"] = r => r.U16,
        };
        var differences = new List<string>();
        foreach (var (column, value) in columns)
        {
            for (int group = 0; group < rows.Length / groupSize; group++)
            {
                var chunk = pages.Where(p => p.GetProperty("path_in_schema").GetString() == column && p.GetProperty("row_group_id").GetInt32() == group)
                    .OrderBy(p => p.GetProperty("page_id").GetInt32()).ToArray();
                Verify(chunk, rows.Skip(group * groupSize).Take(groupSize).Select(value).ToArray(), $"{column} of row group {group}", differences);
            }
        }

        await Assert.That(differences).IsEmpty();
        // The ids go up. ParquetNeo says UNORDERED, which is always right (a reader that knew the order could look for a value by halves, and that is all it would gain);
        // DESCENDING would be wrong.
        var ids = pages.Where(p => p.GetProperty("path_in_schema").GetString() == "Id").ToArray();
        await Assert.That(ids.All(p => p.GetProperty("boundary_order").GetString() is "UNORDERED" or "ASCENDING")).IsTrue();
        await Assert.That(ids.Length).IsGreaterThan(3 * 3); // several pages in every row group
    }

    /// <summary>What the index of a column chunk must say of its values: the pages tile the rows, count their nulls, and bound what they hold.</summary>
    private static void Verify(JsonElement[] pages, object?[] values, string where, List<string> differences)
    {
        if (pages.Length == 0)
        {
            differences.Add($"{where}: no pages in the index");
            return;
        }

        long previousOffset = -1;
        int expectedFirst = 0;
        foreach (var page in pages)
        {
            int first = (int)page.GetProperty("first_row_index").GetInt64();
            int count = (int)page.GetProperty("num_rows").GetInt64();
            long offset = page.GetProperty("page_offset").GetInt64();
            if (first != expectedFirst || count <= 0 || offset <= previousOffset || page.GetProperty("compressed_page_size").GetInt64() <= 0)
            {
                differences.Add($"{where}: the page at row {first} ({count} rows, offset {offset}) does not follow the one before (row {expectedFirst}, offset {previousOffset})");
                return;
            }

            expectedFirst = first + count;
            previousOffset = offset;
            var part = values.Skip(first).Take(count).ToArray();
            if (page.GetProperty("null_count").GetInt64() != part.Count(v => v is null))
            {
                differences.Add($"{where}, page at row {first}: null count {page.GetProperty("null_count").GetInt64()}, expected {part.Count(v => v is null)}");
            }

            if (Bounds(page, part) is { } difference)
            {
                differences.Add($"{where}, page at row {first}: {difference}");
            }
        }

        if (expectedFirst != values.Length)
        {
            differences.Add($"{where}: the pages hold {expectedFirst} rows, the row group {values.Length}");
        }
    }

    private static string? Bounds(JsonElement page, object?[] part)
    {
        var present = part.Where(v => v is not null && !(v is double d && double.IsNaN(d))).ToArray();
        if (page.GetProperty("null_page").GetBoolean())
        {
            return part.All(v => v is null) ? null : "a page that holds values is a page of nulls";
        }

        string? min = page.GetProperty("min_value").GetString();
        string? max = page.GetProperty("max_value").GetString();
        if (present.Length == 0)
        {
            return null;
        }

        if (min is null || max is null)
        {
            // A page that holds a NaN has no bounds: ParquetNeo leaves them out where a bound could not be told from a NaN, and a reader then cannot skip the page.
            return part.Any(v => v is double d && double.IsNaN(d)) ? null : $"no bounds for a page of {present.Length} values and no NaN ({part.Length} rows)";
        }

        if (present[0] is string)
        {
            // Text is cut short: the bounds bound, as bytes.
            var bytes = present.Select(v => Encoding.UTF8.GetBytes((string)v!)).ToArray();
            var smallest = bytes.Aggregate((a, b) => a.AsSpan().SequenceCompareTo(b) <= 0 ? a : b);
            var largest = bytes.Aggregate((a, b) => a.AsSpan().SequenceCompareTo(b) >= 0 ? a : b);
            return Encoding.UTF8.GetBytes(min!).AsSpan().SequenceCompareTo(smallest) > 0 ? "the minimum is above the smallest value"
                : Encoding.UTF8.GetBytes(max!).AsSpan().SequenceCompareTo(largest) < 0 ? "the maximum is below the largest value" : null;
        }

        var ordered = present.OrderBy(v => v, Comparer<object?>.Create((a, b) => ((IComparable)a!).CompareTo(b))).ToArray();
        return Same(min!, ordered[0]!) && Same(max!, ordered[^1]!) ? null : $"bounds {min}..{max}, expected {ordered[0]}..{ordered[^1]}";
    }

    private static bool Same(string text, object expected) => expected switch
    {
        double number => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture).Equals(number),
        _ => text == Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string ToKey(object value) => value switch
    {
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
    };

    private static List<string> Nested<T>(T[] rows, ParquetWriterOptions options) =>
        Checks.CompareNested(PythonEnvironment.DuckDbDump(Checks.Write(rows, "duckdb-nested-" + typeof(T).Name, options, 128)), rows);
}
