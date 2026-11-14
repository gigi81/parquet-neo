using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Filtering;

namespace ParquetNeo.Tests.PageIndex;

/// <summary>A column read for only some rows gives what reading all of it gives for those rows, whatever the type, the encoding and the nesting.</summary>
public class ColumnSelectionTests
{
    private const int Rows = 3000;

    /// <summary>Everything a column holds, one entry per level entry: the levels, and the value (as text) or null.</summary>
    private sealed record Entries(List<int> Definition, List<int> Repetition, List<string?> Values);

    private static Entries ReadAll(RowGroupReader group, int column, RowSelection? selection)
    {
        using var reader = selection is null ? group.OpenColumn(column) : group.OpenColumn(column, selection);
        var node = reader.Column;
        var result = new Entries([], [], []);
        const int batch = 129;
        var definition = new int[batch];
        var repetition = new int[batch];
        var longs = new long[batch];
        var doubles = new double[batch];
        var bools = new bool[batch];
        var bytes = new ReadOnlyMemory<byte>[batch];
        while (true)
        {
            var defs = node.MaxDefinitionLevel > 0 ? definition.AsSpan() : default;
            var reps = node.MaxRepetitionLevel > 0 ? repetition.AsSpan() : default;
            BatchRead read;
            Func<int, string> text;
            switch (node.Type)
            {
                case PhysicalType.Int64:
                    read = reader.ReadBatch(longs, defs, reps);
                    text = i => longs[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case PhysicalType.Double:
                    read = reader.ReadBatch(doubles, defs, reps);
                    text = i => doubles[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case PhysicalType.Boolean:
                    read = reader.ReadBatch(bools, defs, reps);
                    text = i => bools[i].ToString();
                    break;
                default:
                    read = reader.ReadByteArrays(bytes, defs, reps);
                    text = i => System.Text.Encoding.UTF8.GetString(bytes[i].Span);
                    break;
            }

            if (read.Entries == 0)
            {
                return result;
            }

            int value = 0;
            for (int entry = 0; entry < read.Entries; entry++)
            {
                int d = node.MaxDefinitionLevel > 0 ? definition[entry] : 0;
                result.Definition.Add(d);
                result.Repetition.Add(node.MaxRepetitionLevel > 0 ? repetition[entry] : 0);
                result.Values.Add(d == node.MaxDefinitionLevel ? text(value++) : null);
            }
        }
    }

    /// <summary>The entries of the rows of a selection, taken from all the entries: a row starts where the repetition level is 0.</summary>
    private static Entries Slice(Entries all, RowSelection selection)
    {
        var starts = new List<int>();
        for (int i = 0; i < all.Repetition.Count; i++)
        {
            if (all.Repetition[i] == 0)
            {
                starts.Add(i);
            }
        }

        starts.Add(all.Repetition.Count);
        var result = new Entries([], [], []);
        foreach (var range in selection.Ranges)
        {
            for (long row = range.Start; row < range.End; row++)
            {
                for (int entry = starts[(int)row]; entry < starts[(int)row + 1]; entry++)
                {
                    result.Definition.Add(all.Definition[entry]);
                    result.Repetition.Add(all.Repetition[entry]);
                    result.Values.Add(all.Values[entry]);
                }
            }
        }

        return result;
    }

    private static RowSelection RandomSelection(Random random, int rows)
    {
        var ranges = new List<RowRange>();
        long next = random.Next(0, 200);
        while (next < rows && ranges.Count < 10)
        {
            long count = Math.Min(random.Next(1, 500), rows - next);
            ranges.Add(new RowRange(next, count));
            next += count + random.Next(1, 600);
        }

        return new RowSelection(ranges);
    }

    private static async Task Check(PrimitiveNode node, Action<ColumnWriter> write, ParquetWriterOptions options)
    {
        using var file = FilterFile.Create([node], 1, (_, writer) => write(writer.NextColumn()), options);
        var group = file.OpenRowGroup(0);
        var all = ReadAll(group, 0, null);
        var random = new Random(3);
        for (int attempt = 0; attempt < 25; attempt++)
        {
            var selection = RandomSelection(random, Rows);
            var expected = Slice(all, selection);
            var actual = ReadAll(group, 0, selection);
            await Assert.That(actual.Values).IsEquivalentTo(expected.Values);
            await Assert.That(actual.Definition).IsEquivalentTo(expected.Definition);
            await Assert.That(actual.Repetition).IsEquivalentTo(expected.Repetition);
        }
    }

    private static ParquetWriterOptions Options(bool dictionary, DataPageVersion version = DataPageVersion.V1, ParquetEncoding? encoding = null, string? column = null) =>
        new()
        {
            WritePageIndex = true,
            DataPageSize = 300,
            DictionaryEnabled = dictionary,
            DataPageVersion = version,
            ColumnEncodings = encoding is null ? null : new Dictionary<string, ParquetEncoding> { [column!] = encoding.Value },
        };

    private static int[] Levels(Random random, int count, int nullOneIn) => Enumerable.Range(0, count).Select(_ => random.Next(0, nullOneIn) == 0 ? 0 : 1).ToArray();

    [Test]
    [Arguments(true, DataPageVersion.V1)]
    [Arguments(false, DataPageVersion.V1)]
    [Arguments(true, DataPageVersion.V2)]
    [Arguments(false, DataPageVersion.V2)]
    public async Task OptionalLongs(bool dictionary, DataPageVersion version)
    {
        var random = new Random(1);
        var levels = Levels(random, Rows, 4);
        var values = levels.Where(l => l == 1).Select(_ => (long)random.Next(0, dictionary ? 50 : 1_000_000)).ToArray();
        await Check(FilterFile.Column<long>("v", optional: true), w => w.WriteBatch(values, levels), Options(dictionary, version));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task OptionalStrings(bool dictionary)
    {
        var random = new Random(2);
        var levels = Levels(random, Rows, 3);
        var values = levels.Where(l => l == 1).Select(_ => "text-" + random.Next(0, dictionary ? 40 : 100000)).ToArray();
        await Check(FilterFile.Column<string>("v", optional: true), w => w.WriteStrings(values, levels), Options(dictionary));
    }

    [Test]
    public async Task OptionalStringsWithDeltaEncodings()
    {
        var random = new Random(4);
        var levels = Levels(random, Rows, 3);
        var values = levels.Where(l => l == 1).Select(_ => "delta-" + random.Next(0, 100000)).ToArray();
        foreach (var encoding in new[] { ParquetEncoding.DeltaLengthByteArray, ParquetEncoding.DeltaByteArray })
        {
            await Check(FilterFile.Column<string>("v", optional: true), w => w.WriteStrings(values, levels), Options(false, encoding: encoding, column: "v"));
        }
    }

    [Test]
    public async Task OptionalBooleansAndDoubles()
    {
        var random = new Random(5);
        var levels = Levels(random, Rows, 5);
        int present = levels.Count(l => l == 1);
        var bools = Enumerable.Range(0, present).Select(_ => random.Next(0, 2) == 0).ToArray();
        await Check(FilterFile.Column<bool>("v", optional: true), w => w.WriteBatch(bools, levels), Options(false));

        var doubles = Enumerable.Range(0, present).Select(_ => random.NextDouble() * 100).ToArray();
        await Check(FilterFile.Column<double>("v", optional: true), w => w.WriteBatch(doubles, levels), Options(true));
        await Check(FilterFile.Column<double>("v", optional: true), w => w.WriteBatch(doubles, levels), Options(false, encoding: ParquetEncoding.ByteStreamSplit, column: "v"));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RepeatedLongs(bool dictionary)
    {
        // Each row has 0 to 4 values (an empty list is one entry with definition level 0); a row starts where the repetition level is 0.
        var random = new Random(6);
        var definition = new List<int>();
        var repetition = new List<int>();
        var values = new List<long>();
        for (int row = 0; row < Rows; row++)
        {
            int count = random.Next(0, 5);
            if (count == 0)
            {
                definition.Add(0);
                repetition.Add(0);
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                definition.Add(1);
                repetition.Add(i == 0 ? 0 : 1);
                values.Add(random.Next(0, dictionary ? 30 : 1_000_000));
            }
        }

        var node = new PrimitiveNode("v", PhysicalType.Int64, FieldRepetitionType.Repeated);
        await Check(node, w => w.WriteBatch(values.ToArray(), definition.ToArray(), repetition.ToArray()), Options(dictionary));
    }

    [Test]
    public async Task ColumnsWrittenTogetherHaveTheirIndexInTheFile()
    {
        var ids = Enumerable.Range(0, Rows).Select(i => (long)i).ToArray();
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([FilterFile.Column<long>("a"), FilterFile.Column<long>("b")]), Options(true), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.WriteColumns([w => w.WriteBatch(ids), w => w.WriteBatch(ids.Select(i => i * 2).ToArray())]);
        }

        stream.Position = 0;
        using var file = new ParquetFileReader(stream);
        var rows = file.OpenRowGroup(0);
        var selection = rows.SelectRows(ParquetFilter.Column("b").GreaterThanOrEqual(4000L) & ParquetFilter.Column("b").LessThan(4100L), [0, 1])!;
        await Assert.That(selection.RowCount).IsLessThan(Rows / 4);
        var a = ReadAll(rows, 0, selection).Values.Select(v => long.Parse(v!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var b = ReadAll(rows, 1, selection).Values.Select(v => long.Parse(v!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await Assert.That(a.Count(i => i is >= 2000 and < 2050)).IsEqualTo(50);
        await Assert.That(b.Zip(a, (bb, aa) => bb == aa * 2).All(x => x)).IsTrue();
    }

    [Test]
    public async Task SeveralRowGroupsEachHaveTheirOwnIndex()
    {
        using var file = FilterFile.Create(
            [FilterFile.Column<long>("v")],
            3,
            (group, writer) => writer.NextColumn().WriteBatch(Enumerable.Range(0, 2000).Select(i => (long)(group * 2000) + i).ToArray()),
            Options(false));
        for (int group = 0; group < 3; group++)
        {
            var rows = file.OpenRowGroup(group);
            long target = (group * 2000L) + 1500;
            var selection = rows.SelectRows(ParquetFilter.Column("v").Equal(target), [0])!;
            await Assert.That(selection.RowCount).IsLessThan(200);
            await Assert.That(ReadAll(rows, 0, selection).Values).Contains(target.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task AnIndexThatDoesNotFitItsColumnIsLeftAlone()
    {
        var bytes = FilterFile.CreateBytes(
            [FilterFile.Column<long>("v")],
            1,
            (_, writer) => writer.NextColumn().WriteBatch(Enumerable.Range(0, Rows).Select(i => (long)i).ToArray()),
            Options(true));
        using (var clean = new ParquetFileReader(new MemoryStream(bytes)))
        {
            await Assert.That(clean.OpenRowGroup(0).SelectRows(ParquetFilter.Column("v").Equal(5L), [0])).IsNotNull();
        }

        // Damage the offset index (bytes of the page index lie between the last page and the footer).
        var chunk = new ParquetFileReader(new MemoryStream(bytes)).Metadata.RowGroups[0].Columns[0];
        var damaged = bytes.ToArray();
        for (int i = 0; i < chunk.OffsetIndexLength; i++)
        {
            damaged[chunk.OffsetIndexOffset!.Value + i] = 0xFF;
        }

        using var file = new ParquetFileReader(new MemoryStream(damaged));
        var group = file.OpenRowGroup(0);
        await Assert.That(group.SelectRows(ParquetFilter.Column("v").Equal(5L), [0])).IsNull();
        await Assert.That(ReadAll(group, 0, null).Values.Count).IsEqualTo(Rows);
    }
}
