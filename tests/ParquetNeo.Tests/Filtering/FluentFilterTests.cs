namespace ParquetNeo.Tests.Filtering;

/// <summary>Continuing a filter with <c>And("column")</c> and <c>Or("column")</c> gives the filter that joining the conditions with the operators gives.</summary>
public class FluentFilterTests
{
    private static FilterColumn Col(params string[] path) => ParquetFilter.Column(path);

    private static ParquetFileReader Numbers() => FilterFile.Create([FilterFile.Column<long>("Id")], 3, (group, writer) =>
        writer.NextColumn().WriteBatch(Enumerable.Range(group * 10, 10).Select(i => (long)i).ToArray()));

    [Test]
    public async Task AnAndChain_IsTheFilterOfTheOperator()
    {
        var chained = Col("Age").GreaterThanOrEqual(18).And("City").Equal("Rome");
        var joined = Col("Age").GreaterThanOrEqual(18) & Col("City").Equal("Rome");

        await Assert.That(chained.ToString()).IsEqualTo(joined.ToString());
        await Assert.That(chained.Columns.Select(c => string.Join('.', c))).IsEquivalentTo(new[] { "Age", "City" });
    }

    [Test]
    public async Task AnOrChain_IsTheFilterOfTheOperator()
    {
        var chained = Col("City").Equal("Rome").Or("City").Equal("Milan");
        var joined = Col("City").Equal("Rome") | Col("City").Equal("Milan");

        await Assert.That(chained.ToString()).IsEqualTo(joined.ToString());
    }

    [Test]
    public async Task AChain_IsReadFromLeftToRight_WithNoPrecedence()
    {
        var chained = Col("a").Equal(1).And("b").Equal(2).Or("c").Equal(3);
        var grouped = (Col("a").Equal(1) & Col("b").Equal(2)) | Col("c").Equal(3);
        var other = Col("a").Equal(1) & (Col("b").Equal(2) | Col("c").Equal(3));

        await Assert.That(chained.ToString()).IsEqualTo(grouped.ToString());
        await Assert.That(chained.ToString()).IsNotEqualTo(other.ToString());
    }

    [Test]
    public async Task AFilterThatIsJoinedToAChain_GroupsAsItIs()
    {
        var chained = Col("a").Equal(1).And(Col("b").Equal(2).Or("c").Equal(3));
        var other = Col("a").Equal(1) & (Col("b").Equal(2) | Col("c").Equal(3));

        await Assert.That(chained.ToString()).IsEqualTo(other.ToString());
    }

    [Test]
    public async Task EveryConditionOfAColumn_CanContinueAChain()
    {
        var start = Col("a").Equal(1);

        await Assert.That(start.And("x").Equal(5).ToString()).IsEqualTo((start & Col("x").Equal(5)).ToString());
        await Assert.That(start.And("x").Equal(null).ToString()).IsEqualTo((start & Col("x").IsNull()).ToString());
        await Assert.That(start.And("x").NotEqual(5).ToString()).IsEqualTo((start & Col("x").NotEqual(5)).ToString());
        await Assert.That(start.And("x").LessThan(5).ToString()).IsEqualTo((start & Col("x").LessThan(5)).ToString());
        await Assert.That(start.And("x").LessThanOrEqual(5).ToString()).IsEqualTo((start & Col("x").LessThanOrEqual(5)).ToString());
        await Assert.That(start.And("x").GreaterThan(5).ToString()).IsEqualTo((start & Col("x").GreaterThan(5)).ToString());
        await Assert.That(start.And("x").GreaterThanOrEqual(5).ToString()).IsEqualTo((start & Col("x").GreaterThanOrEqual(5)).ToString());
        await Assert.That(start.And("x").Between(5, 9).ToString()).IsEqualTo((start & Col("x").Between(5, 9)).ToString());
        await Assert.That(start.And("x").In(1, 2, 3).ToString()).IsEqualTo((start & Col("x").In(1, 2, 3)).ToString());
        await Assert.That(start.And("x").IsNull().ToString()).IsEqualTo((start & Col("x").IsNull()).ToString());
        await Assert.That(start.Or("x").IsNotNull().ToString()).IsEqualTo((start | Col("x").IsNotNull()).ToString());
        await Assert.That(start.Or("x").Between(5, 9).ToString()).IsEqualTo((start | Col("x").Between(5, 9)).ToString());
    }

    [Test]
    public async Task AColumnInsideAGroup_IsNamedByItsPath()
    {
        var chained = Col("Name").IsNotNull().And("Address", "City").Equal("Rome");
        var joined = Col("Name").IsNotNull() & Col("Address", "City").Equal("Rome");

        await Assert.That(chained.ToString()).IsEqualTo(joined.ToString());
        await Assert.That(chained.Columns.Select(c => string.Join('.', c))).IsEquivalentTo(new[] { "Name", "Address.City" });
    }

    [Test]
    public async Task AChain_KeepsTheRowGroupsThatTheOperatorsKeep()
    {
        using var file = Numbers();

        var range = Col("Id").GreaterThanOrEqual(10L).And("Id").LessThan(20L);
        var either = Col("Id").LessThan(5L).Or("Id").GreaterThanOrEqual(25L);
        var nothing = Col("Id").GreaterThan(100L).And("Id").LessThan(200L);

        await Assert.That(file.GetMatchingRowGroups(range).ToArray()).IsEquivalentTo(new[] { 1 });
        await Assert.That(file.GetMatchingRowGroups(either).ToArray()).IsEquivalentTo(new[] { 0, 2 });
        await Assert.That(file.GetMatchingRowGroups(nothing).ToArray()).IsEquivalentTo(Array.Empty<int>());
    }

    [Test]
    public async Task ABadPath_IsRefusedWhereTheChainContinues()
    {
        var start = Col("a").Equal(1);

        await Assert.That(() => start.And()).Throws<ArgumentException>();
        await Assert.That(() => start.Or("")).Throws<ArgumentException>();
        await Assert.That(() => start.And((string[])null!)).Throws<ArgumentNullException>();
    }
}
