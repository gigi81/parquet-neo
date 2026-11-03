using System.Buffers.Binary;
using System.Text;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Encodings;

/// <summary>
/// Decodes real pages written by pyarrow (uncompressed, no dictionary; see
/// tests/fixtures/generate_plain_fixtures.py) with the PLAIN and RLE decoders, and compares the
/// values with the formulas the generator used.
/// </summary>
public class PlainFixtureTests
{
    private const int Rows = 1000;
    private const string V1 = "pyarrow-plain-v1.parquet";
    private const string V2 = "pyarrow-plain-v2.parquet";

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task DefinitionLevels_DecodeToTheNullPattern(string fixture)
    {
        var page = ReadPage(fixture, "int32");

        var levels = DecodeLevels(page);

        var expected = Enumerable.Range(0, Rows).Select(i => i % 7 == 0 ? 0 : 1).ToArray();
        await Assert.That(levels.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task DefinitionLevels_HandleALongNullStretchFollowedByNoise(string fixture)
    {
        var levels = DecodeLevels(ReadPage(fixture, "int64"));

        var expected = Enumerable.Range(0, Rows).Select(i => i >= 100 && i < 600 ? 0 : 1).ToArray();
        await Assert.That(levels.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task Int32_DecodesPlainValues(string fixture)
    {
        await AssertColumn(fixture, "dense_int", ReadInt32s, i => i * 3 - 500, isNull: _ => false);
        await AssertColumn(fixture, "int32", ReadInt32s, i => i * 3 - 500, isNull: i => i % 7 == 0);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task Int64_DecodesPlainValues(string fixture)
    {
        await AssertColumn(fixture, "int64", ReadInt64s, i => i * 1_000_003L - 7, isNull: i => i >= 100 && i < 600);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task FloatingPoint_DecodesPlainValues(string fixture)
    {
        await AssertColumn(fixture, "float", ReadSingles, i => (float)(i * 0.5 - 3.25), isNull: i => i % 11 == 0);
        await AssertColumn(fixture, "double", ReadDoubles, i => i * 0.125 + 1e-3, isNull: i => i % 13 == 0);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task Booleans_DecodeWhicheverEncodingTheFileUses(string fixture)
    {
        // PLAIN in v1 for "flag"; RLE for "flag_rle" in both files and for "flag" in v2.
        await AssertColumn(fixture, "flag", ReadBooleans, i => i % 3 == 0 || i % 5 == 0, isNull: i => i % 17 == 0);
        await AssertColumn(fixture, "flag_rle", ReadBooleans, i => i % 100 < 70, isNull: _ => false);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task BooleanEncodings_AreWhatTheTestsAssume(string fixture)
    {
        var flag = ReadPage(fixture, "flag");
        var flagRle = ReadPage(fixture, "flag_rle");

        await Assert.That(flag.Encoding).IsEqualTo(fixture == V1 ? ParquetEncoding.Plain : ParquetEncoding.Rle);
        await Assert.That(flagRle.Encoding).IsEqualTo(ParquetEncoding.Rle);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task ByteArrays_DecodeToRangesIntoThePage(string fixture)
    {
        await AssertColumn(fixture, "text", ReadStrings,
            i => string.Concat(Enumerable.Repeat($"value-{i}", i % 4)), isNull: i => i % 9 == 0);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task FixedLengthByteArrays_DecodeAsBackToBackValues(string fixture)
    {
        await AssertColumn(fixture, "fixed", ReadFixedValues,
            i => Convert.ToHexString([(byte)(i % 256), 1, 2, 3]), isNull: i => i % 5 == 0);
    }

    [Test]
    [Arguments(V1)]
    [Arguments(V2)]
    public async Task Int96_DecodesLegacyTimestamps(string fixture)
    {
        // pyarrow stores nanoseconds since midnight and the Julian day number (2440588 is 1970-01-01).
        const int unixEpochJulianDay = 2_440_588;
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await AssertColumn(fixture, "ts96", ReadInt96s, i =>
        {
            var timestamp = start.AddSeconds(i * 3601L).AddTicks(i * 10L); // plus i microseconds
            return (
                unixEpochJulianDay + (int)(timestamp.Date - DateTime.UnixEpoch.Date).TotalDays,
                timestamp.TimeOfDay.Ticks * 100L);
        }, isNull: i => i % 10 == 0);
    }

    // ---- helpers ----

    private static async Task AssertColumn<T>(
        string fixture,
        string column,
        Func<FixturePage, int, T[]> decodeValues,
        Func<int, T> expectedValue,
        Func<int, bool> isNull)
    {
        var page = ReadPage(fixture, column);
        var levels = DecodeLevels(page);
        int present = levels.Count(level => level == 1);
        var values = decodeValues(page, present);

        var actual = new List<T?>();
        int next = 0;
        foreach (int level in levels)
        {
            actual.Add(level == 1 ? values[next++] : default);
        }

        var expected = Enumerable.Range(0, Rows).Select(i => isNull(i) ? default : expectedValue(i)).ToArray();
        await Assert.That(actual.Count).IsEqualTo(Rows);
        await Assert.That(actual.AsEnumerable().SequenceEqual(expected)).IsTrue();
        await Assert.That(levels.Count(level => level == 0)).IsEqualTo(Enumerable.Range(0, Rows).Count(isNull));
    }

    private static int[] ReadInt32s(FixturePage page, int count)
    {
        var values = new int[count];
        PlainDecoder.ReadInt32s(page.Values, values);
        return values;
    }

    private static long[] ReadInt64s(FixturePage page, int count)
    {
        var values = new long[count];
        PlainDecoder.ReadInt64s(page.Values, values);
        return values;
    }

    private static float[] ReadSingles(FixturePage page, int count)
    {
        var values = new float[count];
        PlainDecoder.ReadSingles(page.Values, values);
        return values;
    }

    private static double[] ReadDoubles(FixturePage page, int count)
    {
        var values = new double[count];
        PlainDecoder.ReadDoubles(page.Values, values);
        return values;
    }

    private static (int JulianDay, long NanosOfDay)[] ReadInt96s(FixturePage page, int count)
    {
        var values = new Int96[count];
        PlainDecoder.ReadInt96s(page.Values, values);
        return values.Select(v => (v.JulianDay, v.NanosOfDay)).ToArray();
    }

    private static string[] ReadStrings(FixturePage page, int count)
    {
        var ranges = new ByteRange[count];
        PlainDecoder.ReadByteArrays(page.Values, ranges);
        return ranges.Select(r => Encoding.UTF8.GetString(page.Values, r.Offset, r.Length)).ToArray();
    }

    private static string[] ReadFixedValues(FixturePage page, int count)
    {
        var values = new string[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = Convert.ToHexString(PlainDecoder.ReadFixedLengthByteArrays(page.Values, count, 4).Slice(i * 4, 4));
        }

        return values;
    }

    /// <summary>Booleans are PLAIN, or RLE with a 4-byte length prefix; the page header says which.</summary>
    private static bool[] ReadBooleans(FixturePage page, int count)
    {
        var values = new bool[count];
        if (page.Encoding == ParquetEncoding.Plain)
        {
            PlainDecoder.ReadBooleans(page.Values, values);
            return values;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(page.Values);
        var decoded = new int[count];
        new RleBitPackedDecoder(page.Values.AsSpan(4, length), 1).ReadValues(decoded);
        return decoded.Select(v => v == 1).ToArray();
    }

    private static int[] DecodeLevels(FixturePage page)
    {
        var levels = new int[page.NumValues];
        int read = new RleBitPackedDecoder(page.DefinitionLevels, bitWidth: 1).ReadValues(levels);
        if (read != page.NumValues)
        {
            throw new InvalidOperationException($"Read {read} definition levels, expected {page.NumValues}.");
        }

        return levels;
    }

    private static FixturePage ReadPage(string fixture, string column)
    {
        var file = File.ReadAllBytes(TestBytes.FixturePath(fixture));
        var metadata = FileMetaData.Deserialize(TestBytes.ReadFooterBytes(file));
        var chunk = metadata.RowGroups[0].Columns.Single(c => c.MetaData!.PathInSchema[0] == column).MetaData!;

        var header = PageHeader.Deserialize(file.AsSpan((int)chunk.DataPageOffset), out int headerLength);
        var page = file.AsMemory((int)chunk.DataPageOffset + headerLength, header.CompressedPageSize).ToArray();

        if (header.DataPageHeaderV2 is { } v2)
        {
            // v2: levels first, without a length prefix; this file is uncompressed so the rest is the values.
            return new FixturePage(
                v2.NumValues,
                v2.Encoding,
                page.AsSpan(v2.RepetitionLevelsByteLength, v2.DefinitionLevelsByteLength).ToArray(),
                page[(v2.RepetitionLevelsByteLength + v2.DefinitionLevelsByteLength)..]);
        }

        // v1: flat optional column, so only definition levels, prefixed with their 4-byte length.
        int levelsLength = BinaryPrimitives.ReadInt32LittleEndian(page);
        return new FixturePage(
            header.DataPageHeader!.NumValues,
            header.DataPageHeader.Encoding,
            page.AsSpan(4, levelsLength).ToArray(),
            page[(4 + levelsLength)..]);
    }

    private sealed record FixturePage(int NumValues, ParquetEncoding Encoding, byte[] DefinitionLevels, byte[] Values);
}
