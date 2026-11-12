using System.Text.RegularExpressions;

namespace ParquetNeo.Enums.Tests;

/// <summary>The values of the enumerations of the format are written into files, so they must be those of parquet.thrift.</summary>
public partial class SpecEnumTests
{
    private static readonly string Spec = StripComments(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "specs", "parquet.thrift")));

    [Test]
    [Arguments("Type", typeof(PhysicalType))]
    [Arguments("ConvertedType", typeof(ConvertedType))]
    [Arguments("FieldRepetitionType", typeof(FieldRepetitionType))]
    [Arguments("Encoding", typeof(ParquetEncoding))]
    [Arguments("CompressionCodec", typeof(CompressionCodec))]
    [Arguments("PageType", typeof(PageType))]
    public async Task AnEnumOfTheFormat_HasTheNamesAndValuesOfTheSpecification(string thriftName, Type enumType)
    {
        var expected = ThriftEnum(thriftName);
        var actual = Enum.GetValues(enumType).Cast<object>().ToDictionary(v => Normalize(v.ToString()!), v => Convert.ToInt32(v));

        await Assert.That(actual.Count).IsEqualTo(expected.Count).Because($"{enumType.Name} has as many values as {thriftName}");
        foreach (var (name, value) in expected)
        {
            await Assert.That(actual.ContainsKey(name)).IsTrue().Because($"{enumType.Name} has a value for {name}");
            await Assert.That(actual[name]).IsEqualTo(value).Because($"{enumType.Name}.{name}");
        }
    }

    [Test]
    public async Task TheTimeUnits_AreTheFieldIdsOfTheUnion()
    {
        var expected = ThriftUnion("TimeUnit");

        foreach (var value in Enum.GetValues<TimeUnit>())
        {
            await Assert.That(expected.TryGetValue(Normalize(value.ToString()), out int id)).IsTrue().Because(value.ToString());
            await Assert.That((int)value).IsEqualTo(id).Because(value.ToString());
        }
    }

    [Test]
    public async Task TheKindsOfLogicalType_AreTheFieldIdsOfTheUnion_ForTheOnesThatAreSupported()
    {
        var expected = ThriftUnion("LogicalType");

        // UNKNOWN is Null here; Unrecognized stands for a field id this library does not know, which a file may hold.
        foreach (var value in Enum.GetValues<LogicalTypeKind>().Where(v => v != LogicalTypeKind.Unrecognized))
        {
            string name = value == LogicalTypeKind.Null ? "UNKNOWN" : Normalize(value.ToString());
            await Assert.That(expected.TryGetValue(name, out int id)).IsTrue().Because(value.ToString());
            await Assert.That((int)value).IsEqualTo(id).Because(value.ToString());
        }

        LogicalTypeKind unrecognized = LogicalTypeKind.Unrecognized;

        await Assert.That((int)unrecognized).IsEqualTo(0);
    }

    // ---- reading the specification ----

    private static string StripComments(string text) => LineComment().Replace(BlockComment().Replace(text, string.Empty), string.Empty);

    /// <summary>The members of an <c>enum</c> by upper case name without underscores, numbered as thrift numbers them.</summary>
    private static Dictionary<string, int> ThriftEnum(string name)
    {
        var body = Regex.Match(Spec, @"\benum\s+" + name + @"\s*\{(?<body>.*?)\}", RegexOptions.Singleline).Groups["body"].Value;
        var members = new Dictionary<string, int>();
        int next = 0;
        foreach (string member in body.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = member.Split('=', StringSplitOptions.TrimEntries);
            int value = parts.Length == 2 ? int.Parse(parts[1]) : next;
            members[Normalize(parts[0])] = value;
            next = value + 1;
        }

        return members;
    }

    /// <summary>The members of a <c>union</c>: its field ids by name.</summary>
    private static Dictionary<string, int> ThriftUnion(string name)
    {
        var body = Regex.Match(Spec, @"\bunion\s+" + name + @"\s*\{(?<body>.*?)\}", RegexOptions.Singleline).Groups["body"].Value;
        return Regex.Matches(body, @"(?<id>\d+)\s*:\s*\w+\s+(?<name>\w+)").ToDictionary(m => Normalize(m.Groups["name"].Value), m => int.Parse(m.Groups["id"].Value));
    }

    /// <summary>Upper case without underscores: <c>FIXED_LEN_BYTE_ARRAY</c> and <c>FixedLenByteArray</c> are the same name.</summary>
    private static string Normalize(string name) => name.Replace("_", string.Empty).ToUpperInvariant();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();
}
