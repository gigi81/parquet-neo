using System.Reflection;
using System.Text.Json;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;

namespace ParquetNeo.IntegrationTests;

/// <summary>What the tests of ParquetNeo files read by Python do with them: write rows, have pyarrow read the file, and compare.</summary>
internal static class Checks
{
    public static string Write<T>(IEnumerable<T> rows, string name, ParquetWriterOptions? writer = null, int rowGroupSize = 1000)
    {
        string path = PythonEnvironment.NewFile(name);
        using var stream = File.Create(path);
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = Readers.Writer(writer), RowGroupSize = rowGroupSize });
        return path;
    }

    /// <summary>The differences between what pyarrow read of the file and the rows: the columns of the class are compared by their properties.</summary>
    public static List<string> CompareFlat<T>(JsonElement dump, IReadOnlyList<T> rows)
    {
        var differences = new List<string>();
        var columns = dump.GetProperty("columns");
        var nodes = ParquetSerializer.GetSchema<T>().Columns;
        var names = columns.EnumerateObject().Select(p => p.Name).ToArray();
        if (!names.SequenceEqual(nodes.Select(n => n.Name)))
        {
            differences.Add($"columns: expected {string.Join(",", nodes.Select(n => n.Name))}, python read {string.Join(",", names)}");
            return differences;
        }

        foreach (var node in nodes)
        {
            var property = typeof(T).GetProperty(node.Name, BindingFlags.Public | BindingFlags.Instance)!;
            var python = columns.GetProperty(node.Name);
            if (python.GetArrayLength() != rows.Count)
            {
                differences.Add($"{node.Name}: {python.GetArrayLength()} values, expected {rows.Count}");
                continue;
            }

            for (int i = 0; i < rows.Count && differences.Count < 10; i++)
            {
                if (Values.Compare(python[i], property.GetValue(rows[i]), node) is { } difference)
                {
                    differences.Add($"{node.Name}[{i}]: {difference}");
                }
            }
        }

        return differences;
    }

    /// <summary>The differences between what pyarrow read of the file and the nested rows: the rows as JSON against the columns of Python.</summary>
    public static List<string> CompareNested<T>(JsonElement dump, IReadOnlyList<T> rows)
    {
        var differences = new List<string>();
        var actual = JsonSerializer.SerializeToElement(rows);
        foreach (var column in dump.GetProperty("columns").EnumerateObject())
        {
            if (column.Value.GetArrayLength() != rows.Count)
            {
                differences.Add($"{column.Name}: {column.Value.GetArrayLength()} values, expected {rows.Count}");
                continue;
            }

            for (int i = 0; i < rows.Count && differences.Count < 10; i++)
            {
                if (Values.Compare(column.Value[i], actual[i].GetProperty(column.Name), $"{column.Name}[{i}]") is { } difference)
                {
                    differences.Add(difference);
                }
            }
        }

        return differences;
    }

    /// <summary>The name pyarrow gives a codec.</summary>
    public static string PythonName(CompressionCodec codec) => codec switch
    {
        CompressionCodec.Uncompressed => "UNCOMPRESSED",
        CompressionCodec.Snappy => "SNAPPY",
        CompressionCodec.Gzip => "GZIP",
        CompressionCodec.Zstd => "ZSTD",
        CompressionCodec.Lz4Raw => "LZ4", // pyarrow gives LZ4_RAW the name of the deprecated, Hadoop framed LZ4; reading the data is what shows that it is the raw one
        _ => codec.ToString().ToUpperInvariant(),
    };

    /// <summary>The name pyarrow gives an encoding: <c>DeltaBinaryPacked</c> is <c>DELTA_BINARY_PACKED</c>.</summary>
    public static string PythonName(ParquetEncoding encoding) =>
        string.Concat(encoding.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static JsonElement ColumnChunk(JsonElement dump, int rowGroup, string path) =>
        dump.GetProperty("metadata").GetProperty("row_groups")[rowGroup].GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("path").GetString() == path);

    public static IEnumerable<JsonElement> ColumnChunks(JsonElement dump) =>
        dump.GetProperty("metadata").GetProperty("row_groups").EnumerateArray().SelectMany(g => g.GetProperty("columns").EnumerateArray());
}
