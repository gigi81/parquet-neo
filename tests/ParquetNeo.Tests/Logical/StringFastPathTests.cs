using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Logical;

/// <summary>The string paths that are tuned for speed: strings of a dictionary shared between rows, batches over pages, one-pass encoding and the statistics of a dictionary column.</summary>
public class StringFastPathTests
{
    private static PrimitiveNode Text(FieldRepetitionType repetition = FieldRepetitionType.Required) =>
        new("text", PhysicalType.ByteArray, repetition, LogicalType.String);

    private static byte[] Write(PrimitiveNode node, Action<ColumnWriter> write, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            write(group.NextColumn());
        }

        return stream.ToArray();
    }

    private static string Decode(object? bound) => System.Text.Encoding.UTF8.GetString((byte[])bound!);

    private static ColumnReader Open(byte[] bytes) => ReadHelper.Open(bytes).OpenRowGroup(0).OpenColumn(0);

    [Test]
    public async Task TheEntriesOfADictionary_AreOneStringEachHoweverOftenTheyRepeat()
    {
        string[] values = [.. Enumerable.Range(0, 300).Select(i => new string(['k', (char)('a' + i % 3)]))];
        var bytes = Write(Text(), w => w.WriteStrings(values), new ParquetWriterOptions { DictionaryEnabled = true });

        var back = new string[300];
        Open(bytes).ReadStrings(back);

        await Assert.That(back).IsEquivalentTo(values);
        for (int i = 3; i < back.Length; i++)
        {
            await Assert.That(ReferenceEquals(back[i], back[i % 3])).IsTrue().Because($"row {i}");
        }
    }

    [Test]
    public async Task AColumnWithNulls_ReadsDictionaryStringsToo()
    {
        string?[] all = [.. Enumerable.Range(0, 100).Select(i => i % 4 == 0 ? null : "name-" + i % 5)];
        string[] present = [.. all.Where(s => s is not null)!];
        int[] levels = [.. all.Select(s => s is null ? 0 : 1)];
        var bytes = Write(Text(FieldRepetitionType.Optional), w => w.WriteStrings(present, levels));

        var back = new string[100];
        var backLevels = new int[100];
        var read = Open(bytes).ReadStrings(back, backLevels);

        await Assert.That(read.Entries).IsEqualTo(100);
        await Assert.That(read.Values).IsEqualTo(present.Length);
        await Assert.That(back.Take(read.Values)).IsEquivalentTo(present);
        await Assert.That(backLevels).IsEquivalentTo(levels);
    }

    [Test]
    public async Task ABatch_ReadsAcrossPages_InOneCall()
    {
        string[] values = [.. Enumerable.Range(0, 5000).Select(i => "value " + i)];
        var options = new ParquetWriterOptions { DataPageSize = 1000, DictionaryEnabled = false };
        var bytes = Write(Text(), w => w.WriteStrings(values), options);

        var back = new string[5000];
        var read = Open(bytes).ReadStrings(back);

        await Assert.That(read.Entries).IsEqualTo(5000);
        await Assert.That(read.Values).IsEqualTo(5000);
        await Assert.That(back).IsEquivalentTo(values);
    }

    [Test]
    public async Task ABatch_ReadsAcrossDictionaryPages_InSmallBatches()
    {
        string[] values = [.. Enumerable.Range(0, 3000).Select(i => "v" + i % 40)];
        var options = new ParquetWriterOptions { DataPageSize = 500, DictionaryEnabled = true };
        var bytes = Write(Text(), w => w.WriteStrings(values), options);

        using var reader = Open(bytes);
        var result = new List<string>();
        var batch = new string[77];
        while (true)
        {
            var read = reader.ReadStrings(batch);
            if (read.Entries == 0)
            {
                break;
            }

            result.AddRange(batch.Take(read.Values));
        }

        await Assert.That(result).IsEquivalentTo(values);
    }

    [Test]
    public async Task InvalidUtf8InADictionary_IsAValueConversionException()
    {
        byte[][] values = [[0x61], [0xFF, 0xFE], [0x61]];
        var bytes = Write(Text(), w => w.WriteByteArrays([.. values.Select(v => (ReadOnlyMemory<byte>)v)]), new ParquetWriterOptions { DictionaryEnabled = true });

        var exception = Assert.Throws<ValueConversionException>(() => Open(bytes).ReadStrings(new string[3]));

        await Assert.That(exception.Message).Contains("'text'").And.Contains("UTF-8");
    }

    [Test]
    public async Task InvalidUtf8InAPlainPage_IsAValueConversionException()
    {
        byte[][] values = [[0x61], [0xC3, 0x28], [0x61]];
        var bytes = Write(Text(), w => w.WriteByteArrays([.. values.Select(v => (ReadOnlyMemory<byte>)v)]), new ParquetWriterOptions { DictionaryEnabled = false });

        await Assert.That(() => Open(bytes).ReadStrings(new string[3])).Throws<ValueConversionException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StringsAroundTheOnePassLimit_AndWithMultiByteCharacters_RoundTrip(bool dictionary)
    {
        string[] values =
        [
            "",
            "a",
            new string('x', 1023),
            new string('x', 1024),
            new string('x', 1025),
            new string('€', 1024),
            new string('€', 1025),
            string.Concat(Enumerable.Repeat("é😀", 512)),
            string.Concat(Enumerable.Repeat("é😀", 513)),
            new string('é', 5000),
            "x",
        ];
        var bytes = Write(Text(), w => w.WriteStrings(values), new ParquetWriterOptions { DictionaryEnabled = dictionary });

        var back = new string[values.Length];
        Open(bytes).ReadStrings(back);

        await Assert.That(back).IsEquivalentTo(values);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ALoneSurrogate_BecomesTheReplacementCharacter_InBothEncodings(bool dictionary)
    {
        var bytes = Write(Text(), w => w.WriteStrings(["a\uD800b", "\uDFFF"]), new ParquetWriterOptions { DictionaryEnabled = dictionary });

        var back = new string[2];
        Open(bytes).ReadStrings(back);

        await Assert.That(back[0]).IsEqualTo("a�b");
        await Assert.That(back[1]).IsEqualTo("�");
    }

    [Test]
    public async Task TwoStringsThatEncodeAlike_ShareADictionaryEntry()
    {
        // Both lone surrogates become U+FFFD, so the dictionary holds one entry for the two.
        var dictionaryBytes = Write(Text(), w => w.WriteStrings(["\uD800", "\uDFFF", "�"]), new ParquetWriterOptions { DictionaryEnabled = true });
        var plainBytes = Write(Text(), w => w.WriteStrings(["\uD800", "\uDFFF", "�"]), new ParquetWriterOptions { DictionaryEnabled = false });

        var fromDictionary = new string[3];
        var fromPlain = new string[3];
        Open(dictionaryBytes).ReadStrings(fromDictionary);
        Open(plainBytes).ReadStrings(fromPlain);

        await Assert.That(fromDictionary).IsEquivalentTo(fromPlain);
        await Assert.That(fromDictionary).IsEquivalentTo(new[] { "�", "�", "�" });
    }

    [Test]
    public async Task TheStatisticsOfADictionaryColumn_AreThoseOfThePlainColumn()
    {
        string[] values = [.. Enumerable.Range(0, 2000).Select(i => "name-" + (i * 7919 % 613))];
        var dictionaryBytes = Write(Text(), w => w.WriteStrings(values), new ParquetWriterOptions { DictionaryEnabled = true });
        var plainBytes = Write(Text(), w => w.WriteStrings(values), new ParquetWriterOptions { DictionaryEnabled = false });

        using var dictionaryFile = ReadHelper.Open(dictionaryBytes);
        using var plainFile = ReadHelper.Open(plainBytes);
        var fromDictionary = dictionaryFile.OpenRowGroup(0).GetStatistics(0)!;
        var fromPlain = plainFile.OpenRowGroup(0).GetStatistics(0)!;

        await Assert.That(Decode(fromDictionary.Min)).IsEqualTo(Decode(fromPlain.Min));
        await Assert.That(Decode(fromDictionary.Max)).IsEqualTo(Decode(fromPlain.Max));
        await Assert.That(Decode(fromDictionary.Min)).IsEqualTo(values.OrderBy(v => v, StringComparer.Ordinal).First());
        await Assert.That(Decode(fromDictionary.Max)).IsEqualTo(values.OrderBy(v => v, StringComparer.Ordinal).Last());
    }

    [Test]
    public async Task TheStatisticsOfAColumnThatFallsBackFromTheDictionary_StillCoverEveryValue()
    {
        // Distinct values until the dictionary is given up, then a value past the old extremes in the plain pages.
        string[] values = [.. Enumerable.Range(0, 20_000).Select(i => "key-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)), "zzz-last", "aaa-first"];
        var options = new ParquetWriterOptions { DictionaryPageSizeLimit = 4096, DataPageSize = 8192 };
        var bytes = Write(Text(), w => w.WriteStrings(values), options);

        using var file = ReadHelper.Open(bytes);
        var statistics = file.OpenRowGroup(0).GetStatistics(0)!;

        await Assert.That(Decode(statistics.Min)).IsEqualTo("aaa-first");
        await Assert.That(Decode(statistics.Max)).IsEqualTo("zzz-last");
    }
}
