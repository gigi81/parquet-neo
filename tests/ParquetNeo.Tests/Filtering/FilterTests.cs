using ParquetNeo.ColumnTypes;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Filtering;

/// <summary>Which row groups of a file a filter keeps, from the statistics of known data.</summary>
public class FilterTests
{
    private static FilterColumn Col(string name) => ParquetFilter.Column(name);

    private static async Task Expect(ParquetFileReader file, ParquetFilter filter, params int[] groups) =>
        await Assert.That(file.GetMatchingRowGroups(filter).ToArray()).IsEquivalentTo(groups);

    /// <summary>Three row groups of ten numbers: 0 to 9, 10 to 19, 20 to 29.</summary>
    private static ParquetFileReader Numbers() => FilterFile.Create([FilterFile.Column<long>("Id")], 3, (group, writer) =>
        writer.NextColumn().WriteBatch(Enumerable.Range(group * 10, 10).Select(i => (long)i).ToArray()));

    [Test]
    public async Task Comparisons_KeepTheRowGroupsWhoseRangeCanHoldTheValue()
    {
        using var file = Numbers();

        await Expect(file, Col("Id").Equal(15L), 1);
        await Expect(file, Col("Id").Equal(10), 1);
        await Expect(file, Col("Id").Equal(9), 0);
        await Expect(file, Col("Id").Equal(100));
        await Expect(file, Col("Id").GreaterThan(19), 2);
        await Expect(file, Col("Id").GreaterThanOrEqual(19), 1, 2);
        await Expect(file, Col("Id").LessThan(10), 0);
        await Expect(file, Col("Id").LessThanOrEqual(10), 0, 1);
        await Expect(file, Col("Id").GreaterThan(29));
        await Expect(file, Col("Id").LessThan(0));
        await Expect(file, Col("Id").Between(5, 12), 0, 1);
        await Expect(file, Col("Id").In(2L, 25L), 0, 2);
        await Expect(file, Col("Id").NotEqual(15), 0, 1, 2);
    }

    [Test]
    public async Task Combinations_AreAsTheLogicOfTheirParts()
    {
        using var file = Numbers();

        await Expect(file, Col("Id").LessThan(15) & Col("Id").GreaterThan(5), 0, 1);
        await Expect(file, Col("Id").LessThan(5) & Col("Id").GreaterThan(25));
        await Expect(file, Col("Id").LessThan(5) | Col("Id").GreaterThan(25), 0, 2);
        await Expect(file, !Col("Id").LessThan(10), 1, 2);
        await Expect(file, !(Col("Id").LessThan(10) | Col("Id").GreaterThanOrEqual(20)), 1);
        await Expect(file, ParquetFilter.AllOf(Col("Id").GreaterThan(5), Col("Id").LessThan(25), Col("Id").NotEqual(12)), 0, 1, 2);
        await Expect(file, ParquetFilter.AnyOf(Col("Id").Equal(1), Col("Id").Equal(21)), 0, 2);
        await Expect(file, !!Col("Id").Equal(15), 1);
    }

    [Test]
    public async Task ARowGroupWhereEveryRowSatisfiesAComparison_IsKnownToBeNoMatchForItsNegation()
    {
        using var file = FilterFile.Create([FilterFile.Column<int>("C")], 2, (group, writer) =>
            writer.NextColumn().WriteBatch(group == 0 ? new[] { 5, 5, 5 } : new[] { 5, 6, 7 }));

        await Expect(file, Col("C").Equal(5), 0, 1);
        await Expect(file, Col("C").NotEqual(5), 1);
        await Expect(file, Col("C").LessThan(8).Not());
        await Expect(file, Col("C").GreaterThan(4).Not());
        await Expect(file, Col("C").GreaterThan(5).Not(), 0, 1);
    }

    [Test]
    public async Task Nulls_AreNeverEqualToAnything_AndAreNotEqualToEverything()
    {
        // Group 0 is all null, group 1 has some, group 2 has none.
        using var file = FilterFile.Create([FilterFile.Column<int>("N", optional: true)], 3, (group, writer) =>
        {
            var column = writer.NextColumn();
            switch (group)
            {
                case 0: column.WriteBatch(ReadOnlySpan<int>.Empty, new[] { 0, 0, 0, 0 }); break;
                case 1: column.WriteBatch(new[] { 1, 2, 4 }, new[] { 1, 1, 0, 1 }); break;
                default: column.WriteBatch(new[] { 7, 8, 9, 10 }, new[] { 1, 1, 1, 1 }); break;
            }
        });

        await Expect(file, Col("N").IsNull(), 0, 1);
        await Expect(file, Col("N").IsNotNull(), 1, 2);
        await Expect(file, Col("N").Equal(null), 0, 1);
        await Expect(file, Col("N").NotEqual(null), 1, 2);
        await Expect(file, Col("N").Equal(2), 1);
        await Expect(file, Col("N").GreaterThan(0), 1, 2);
        await Expect(file, Col("N").GreaterThan(8), 2);

        // A null is not equal to 2, so a row group with one is kept; with the rows 7 to 10 only the numbers decide.
        await Expect(file, Col("N").NotEqual(2), 0, 1, 2);
        await Expect(file, Col("N").LessThan(5).Not(), 0, 1, 2);
        await Expect(file, Col("N").LessThan(100).Not(), 0, 1);
        await Expect(file, Col("N").IsNull().Not() & Col("N").GreaterThan(5), 2);
    }

    [Test]
    public async Task ARequiredColumn_HasNoNulls_WhateverTheStatisticsSay()
    {
        using var file = Numbers();

        await Expect(file, Col("Id").IsNull());
        await Expect(file, Col("Id").IsNotNull(), 0, 1, 2);
    }

    [Test]
    public async Task UnsignedIntegers_AreComparedAsUnsigned()
    {
        using var file = FilterFile.Create([FilterFile.Column<uint>("U")], 2, (group, writer) =>
            writer.NextColumn().WriteBatch(group == 0 ? new uint[] { 1, 2, 3 } : new uint[] { 3_000_000_000, 3_000_000_001 }));

        await Expect(file, Col("U").GreaterThan(2_000_000_000u), 1);
        await Expect(file, Col("U").GreaterThan(5), 1);
        await Expect(file, Col("U").LessThan(10u), 0);
        await Expect(file, Col("U").Equal(3_000_000_001u), 1);
        await Expect(file, Col("U").Equal(-1));
        await Expect(file, Col("U").GreaterThan(-1), 0, 1);
    }

    [Test]
    public async Task Text_IsComparedByItsUtf8Bytes()
    {
        string[][] groups = [["apple", "avocado"], ["banana", "blueberry"], ["cherry", "citrus"]];
        using var file = FilterFile.Create([FilterFile.Column<string>("S")], 3, (group, writer) => writer.NextColumn().WriteStrings(groups[group]));

        await Expect(file, Col("S").Equal("banana"), 1);
        await Expect(file, Col("S").Equal("blackberry"), 1);
        await Expect(file, Col("S").Equal("zebra"));
        await Expect(file, Col("S").GreaterThan("b"), 1, 2);
        await Expect(file, Col("S").GreaterThanOrEqual("cherry"), 2);
        await Expect(file, Col("S").LessThan("b"), 0);
        await Expect(file, Col("S").Between("avo", "bl"), 0, 1);
        await Expect(file, Col("S").In("apple", "citrus", "nothing"), 0, 2);
    }

    [Test]
    public async Task Floats_AreComparedByValue_AndANaNIsNeverEqualToAnything()
    {
        using var file = FilterFile.Create([FilterFile.Column<double>("D")], 3, (group, writer) =>
            writer.NextColumn().WriteBatch(group switch { 0 => new[] { 1.0, 2.0 }, 1 => [10.0, double.NaN], _ => [20.0, 21.0] }));

        await Expect(file, Col("D").GreaterThan(5.5), 1, 2);
        await Expect(file, Col("D").LessThan(5), 0);
        await Expect(file, Col("D").Equal(10), 1);
        await Expect(file, Col("D").Equal(double.NaN));
        await Expect(file, Col("D").GreaterThan(double.NaN));
        await Expect(file, Col("D").NotEqual(double.NaN), 0, 1, 2);
        await Expect(file, Col("D").GreaterThan(15f), 2);

        // The row group with a NaN is kept for the negation, since the NaN is not less than 15.
        await Expect(file, Col("D").LessThan(15).Not(), 1, 2);
        await Expect(file, Col("D").LessThan(1000).Not(), 1);
    }

    [Test]
    public async Task DatesTimesTimestampsAndDecimals_AreComparedAsTheTypesAre()
    {
        var columns = new[]
        {
            FilterFile.Column<DateOnly>("Day"),
            FilterFile.Column<TimeOnly>("Clock"),
            FilterFile.Column<DateTime>("Moment"),
            FilterFile.Column<DateTimeOffset>("Offset"),
            ColumnTypeRegistry.Get<decimal>().Configure(new ColumnSettings { Precision = 12, Scale = 2 }).CreateNode("Money", FieldRepetitionType.Required),
        };
        var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        using var file = FilterFile.Create(columns, 3, (group, writer) =>
        {
            var from = start.AddDays(group * 10);
            writer.NextColumn().WriteBatch(new[] { DateOnly.FromDateTime(from), DateOnly.FromDateTime(from.AddDays(9)) });
            writer.NextColumn().WriteBatch(new[] { new TimeOnly(group * 4, 0), new TimeOnly(group * 4 + 3, 59, 59) });
            writer.NextColumn().WriteBatch(new[] { from, from.AddDays(9).AddHours(23) });
            writer.NextColumn().WriteBatch(new[] { new DateTimeOffset(from), new DateTimeOffset(from.AddDays(9)) });
            writer.NextColumn().WriteBatch(new[] { group * 100m + 0.25m, group * 100m + 99.75m });
        });

        await Expect(file, Col("Day").Equal(new DateOnly(2024, 3, 15)), 1);
        await Expect(file, Col("Day").GreaterThan(new DateOnly(2024, 3, 21)), 2);
        await Expect(file, Col("Day").LessThan(new DateTime(2024, 3, 11)), 0);
        await Expect(file, Col("Clock").Equal(new TimeOnly(5, 30)), 1);
        await Expect(file, Col("Clock").GreaterThan(new TimeOnly(9, 0)), 2);
        await Expect(file, Col("Moment").GreaterThan(new DateTime(2024, 3, 25, 0, 0, 0, DateTimeKind.Utc)), 2);
        await Expect(file, Col("Moment").Equal(start.AddDays(10).AddHours(5)), 1);
        await Expect(file, Col("Moment").LessThanOrEqual(start.AddDays(-1)));
        await Expect(file, Col("Offset").GreaterThanOrEqual(new DateTimeOffset(start.AddDays(20))), 2);
        await Expect(file, Col("Offset").LessThan(new DateTimeOffset(2024, 3, 10, 21, 0, 0, TimeSpan.FromHours(-3))), 0);
        await Expect(file, Col("Money").Equal(150.5m), 1);
        await Expect(file, Col("Money").GreaterThan(199.76m), 2);
        await Expect(file, Col("Money").LessThan(0.25m));
        await Expect(file, Col("Money").Equal(100));
    }

    [Test]
    public async Task GuidsAndEnumsCanOnlyBeComparedForEquality_AndOtherComparisonsKeepTheRowGroups()
    {
        var first = new Guid("00000000-0000-0000-0000-000000000001");
        var second = new Guid("ffffffff-0000-0000-0000-000000000001");
        using var file = FilterFile.Create([FilterFile.Column<Guid>("G"), FilterFile.Column<string>("S")], 2, (group, writer) =>
        {
            writer.NextColumn().WriteBatch(new[] { group == 0 ? first : second });
            writer.NextColumn().WriteStrings(new[] { group == 0 ? "Monday" : "Tuesday" });
        });

        await Expect(file, Col("G").Equal(first), 0);
        await Expect(file, Col("G").Equal(Guid.NewGuid()));
        await Expect(file, Col("G").GreaterThan(second), 0, 1);
        await Expect(file, Col("S").Equal(DayOfWeek.Tuesday), 1);
        await Expect(file, Col("S").Equal(DayOfWeek.Friday));
        await Expect(file, Col("S").GreaterThan(DayOfWeek.Sunday), 0, 1);
    }

    [Test]
    public async Task WhatTheStatisticsCannotTell_KeepsEveryRowGroup()
    {
        var noStatistics = new ParquetWriterOptions { StatisticsEnabled = false };
        using var file = FilterFile.Create([FilterFile.Column<long>("Id"), FilterFile.Column<long>("Other", optional: true)], 2, (group, writer) =>
        {
            writer.NextColumn().WriteBatch(new[] { group * 10L, group * 10L + 1 });
            writer.NextColumn().WriteBatch(new[] { 1L }, new[] { 1, 0 });
        }, noStatistics);

        await Expect(file, Col("Id").Equal(500), 0, 1);
        await Expect(file, Col("Other").IsNull(), 0, 1);

        // A column the file does not have, or a column in a collection, decides nothing.
        using var numbers = Numbers();
        await Expect(numbers, Col("Missing").Equal(1), 0, 1, 2);
        await Expect(numbers, ParquetFilter.Column("Id", "Deeper").Equal(1), 0, 1, 2);
    }

    [Test]
    public async Task AValueThatDoesNotSuitTheColumn_IsAMistake()
    {
        using var file = Numbers();

        await Assert.That(() => file.GetMatchingRowGroups(Col("Id").Equal("text"))).Throws<ArgumentException>();
        await Assert.That(() => file.GetMatchingRowGroups(Col("Id").GreaterThan(1.5))).Throws<ArgumentException>();
        await Assert.That(() => file.GetMatchingRowGroups(Col("Id").Equal(DateTime.UtcNow))).Throws<ArgumentException>();
    }

    [Test]
    public async Task TheFilterIsCheckedForTheRowGroupAsWellAsTheFile()
    {
        using var file = Numbers();

        await Assert.That(file.OpenRowGroup(1).MayMatch(Col("Id").Equal(15))).IsTrue();
        await Assert.That(file.OpenRowGroup(0).MayMatch(Col("Id").Equal(15))).IsFalse();
        await Assert.That(file.OpenRowGroup(2).MayMatch(Col("Id").GreaterThan(5) & Col("Id").LessThan(25))).IsTrue();
    }

    [Test]
    public async Task TheColumnsAFilterNames_AreListed()
    {
        var filter = Col("Id").GreaterThan(1) & (Col("Name").IsNull() | !ParquetFilter.Column("Address", "Zip").Equal(5));

        await Assert.That(filter.Columns.Select(path => string.Join('.', path)).ToArray()).IsEquivalentTo(new[] { "Id", "Name", "Address.Zip" });
    }

    [Test]
    public async Task BuildingAFilter_ChecksItsArguments()
    {
        await Assert.That(() => ParquetFilter.Column()).Throws<ArgumentException>();
        await Assert.That(() => ParquetFilter.Column("")).Throws<ArgumentException>();
        await Assert.That(() => Col("Id").GreaterThan(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => Col("Id").In()).Throws<ArgumentException>();
        await Assert.That(() => ParquetFilter.AllOf()).Throws<ArgumentException>();
        await Assert.That(() => Col("Id").Equal(1).And(null!)).Throws<ArgumentNullException>();
        await Assert.That(ParquetFilter.AllOf(Col("Id").Equal(1)).Columns.Count).IsEqualTo(1);
    }
}
