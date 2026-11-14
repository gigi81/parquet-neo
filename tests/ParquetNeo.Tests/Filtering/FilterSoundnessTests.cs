using ParquetNeo.Filtering;

namespace ParquetNeo.Tests.Filtering;

/// <summary>
/// A filter must never skip a row group that has a row satisfying it. Random data and random filters are checked against a plain
/// evaluation of every row, which has the meaning of the filter in C#: a comparison with a null or a NaN is false, and not negates.
/// </summary>
public class FilterSoundnessTests
{
    private sealed record Row(int? A, double? B, string? C, long D);

    private sealed record Check(ParquetFilter Filter, Func<Row, bool> Holds, string Text);

    private static readonly string[] Letters = ["a", "b", "c", "d", "e", "f", "g", "h"];

    private static List<List<Row>> MakeRowGroups(Random random)
    {
        var groups = new List<List<Row>>();
        long id = 0;
        for (int g = 0; g < 6; g++)
        {
            int count = random.Next(1, 9);
            int baseNumber = random.Next(0, 4) * 5;
            int baseLetter = random.Next(0, 5);
            var rows = new List<Row>();
            for (int i = 0; i < count; i++)
            {
                double? b = random.Next(10) switch { 0 => null, 1 => double.NaN, _ => baseNumber * 1.5 + random.Next(0, 5) * 0.5 };
                rows.Add(new Row(
                    random.Next(4) == 0 ? null : baseNumber + random.Next(0, 6),
                    b,
                    random.Next(4) == 0 ? null : Letters[baseLetter + random.Next(0, 3)],
                    id++ * (random.Next(2) == 0 ? 1 : 3)));
            }

            groups.Add(rows);
        }

        return groups;
    }

    private static Check MakeLeaf(Random random)
    {
        string[] columns = ["A", "B", "C", "D"];
        string column = columns[random.Next(columns.Length)];
        object value = column switch
        {
            "A" => random.Next(-1, 22),
            "B" => random.Next(-1, 32) * 0.5,
            "C" => Letters[random.Next(Letters.Length)],
            _ => (long)random.Next(-1, 40),
        };
        if (column == "B" && random.Next(12) == 0)
        {
            value = double.NaN;
        }

        object? Get(Row row) => column switch { "A" => row.A, "B" => row.B, "C" => row.C, _ => row.D };
        int Compare(object? cell) => column switch
        {
            "A" => ((int)cell!).CompareTo((int)value),
            "B" => ((double)cell!).CompareTo((double)value),
            "C" => string.CompareOrdinal((string)cell!, (string)value),
            _ => ((long)cell!).CompareTo(Convert.ToInt64(value)),
        };
        bool IsNaN(object? cell) => cell is double d && double.IsNaN(d) || value is double v && double.IsNaN(v);

        var target = ParquetFilter.Column(column);
        switch (random.Next(9))
        {
            case 0: return new Check(target.Equal(value), r => Get(r) is { } c && !IsNaN(c) && Compare(c) == 0, $"{column} == {value}");
            case 1: return new Check(target.NotEqual(value), r => !(Get(r) is { } c && !IsNaN(c) && Compare(c) == 0), $"{column} != {value}");
            case 2: return new Check(target.LessThan(value), r => Get(r) is { } c && !IsNaN(c) && Compare(c) < 0, $"{column} < {value}");
            case 3: return new Check(target.LessThanOrEqual(value), r => Get(r) is { } c && !IsNaN(c) && Compare(c) <= 0, $"{column} <= {value}");
            case 4: return new Check(target.GreaterThan(value), r => Get(r) is { } c && !IsNaN(c) && Compare(c) > 0, $"{column} > {value}");
            case 5: return new Check(target.GreaterThanOrEqual(value), r => Get(r) is { } c && !IsNaN(c) && Compare(c) >= 0, $"{column} >= {value}");
            case 6: return new Check(target.IsNull(), r => Get(r) is null, $"{column} is null");
            case 7: return new Check(target.IsNotNull(), r => Get(r) is not null, $"{column} is not null");
            default:
                var other = MakeValueFor(column, random);
                return new Check(
                    target.In(value, other),
                    r => Get(r) is { } c && !(c is double d && double.IsNaN(d)) && (Compare(c) == 0 || (!(value is double nv && double.IsNaN(nv)) && Same(column, c, other))),
                    $"{column} in ({value}, {other})");
        }
    }

    private static object MakeValueFor(string column, Random random) => column switch
    {
        "A" => random.Next(-1, 22),
        "B" => random.Next(-1, 32) * 0.5,
        "C" => Letters[random.Next(Letters.Length)],
        _ => (long)random.Next(-1, 40),
    };

    private static bool Same(string column, object cell, object value) => column switch
    {
        "A" => (int)cell == (int)value,
        "B" => (double)cell == (double)value,
        "C" => string.Equals((string)cell, (string)value, StringComparison.Ordinal),
        _ => (long)cell == Convert.ToInt64(value),
    };

    private static Check MakeFilter(Random random, int depth)
    {
        if (depth == 0 || random.Next(3) == 0)
        {
            return MakeLeaf(random);
        }

        switch (random.Next(3))
        {
            case 0:
                var left = MakeFilter(random, depth - 1);
                var right = MakeFilter(random, depth - 1);
                return new Check(left.Filter & right.Filter, r => left.Holds(r) && right.Holds(r), $"({left.Text} and {right.Text})");
            case 1:
                var first = MakeFilter(random, depth - 1);
                var second = MakeFilter(random, depth - 1);
                return new Check(first.Filter | second.Filter, r => first.Holds(r) || second.Holds(r), $"({first.Text} or {second.Text})");
            default:
                var inner = MakeFilter(random, depth - 1);
                return new Check(!inner.Filter, r => !inner.Holds(r), $"not {inner.Text}");
        }
    }

    private static ParquetFileReader Write(List<List<Row>> groups) =>
        FilterFile.Create(
            [FilterFile.Column<int>("A", optional: true), FilterFile.Column<double>("B", optional: true), FilterFile.Column<string>("C", optional: true), FilterFile.Column<long>("D")],
            groups.Count,
            (index, writer) =>
            {
                var rows = groups[index];
                writer.NextColumn().WriteBatch(rows.Where(r => r.A.HasValue).Select(r => r.A!.Value).ToArray(), rows.Select(r => r.A.HasValue ? 1 : 0).ToArray());
                writer.NextColumn().WriteBatch(rows.Where(r => r.B.HasValue).Select(r => r.B!.Value).ToArray(), rows.Select(r => r.B.HasValue ? 1 : 0).ToArray());
                writer.NextColumn().WriteStrings(rows.Where(r => r.C is not null).Select(r => r.C!).ToArray(), rows.Select(r => r.C is not null ? 1 : 0).ToArray());
                writer.NextColumn().WriteBatch(rows.Select(r => r.D).ToArray());
            });

    [Test]
    public async Task AFilter_NeverSkipsARowGroupThatHasARowThatSatisfiesIt()
    {
        int checkedGroups = 0;
        int skipped = 0;
        int keptWithoutMatch = 0;
        var failures = new List<string>();
        for (int seed = 0; seed < 120; seed++)
        {
            var random = new Random(seed);
            var groups = MakeRowGroups(random);
            using var file = Write(groups);
            for (int n = 0; n < 60; n++)
            {
                var check = MakeFilter(random, depth: 3);
                var kept = file.GetMatchingRowGroups(check.Filter).ToHashSet();
                for (int g = 0; g < groups.Count; g++)
                {
                    bool hasMatch = groups[g].Any(check.Holds);
                    checkedGroups++;
                    if (hasMatch && !kept.Contains(g))
                    {
                        failures.Add($"seed {seed}: skipped group {g} for {check.Text}");
                    }

                    skipped += kept.Contains(g) ? 0 : 1;
                    keptWithoutMatch += !hasMatch && kept.Contains(g) ? 1 : 0;
                }
            }
        }

        await Assert.That(failures.Take(5).ToArray()).IsEmpty();

        // The test is not empty: a fair share of the row groups were skipped, and most of the ones without a match were found out.
        await Assert.That(skipped).IsGreaterThan(checkedGroups / 10);
        await Assert.That(keptWithoutMatch).IsLessThan(checkedGroups / 2);
    }
}
