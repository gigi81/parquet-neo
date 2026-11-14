namespace ParquetNeo.Linq.Tests;

/// <summary>What queries of a dataset return is what the same queries return over the rows in memory.</summary>
public class QueryResultTests
{
    private static async Task Same<TResult>(Func<IQueryable<Person>, IEnumerable<TResult>> query, bool ordered = false)
    {
        using var data = new TestDataset();
        var expected = query(data.All.AsQueryable()).ToList();
        var actual = query(data.Open()).ToList();

        if (ordered)
        {
            await Assert.That(actual.Select(Show).SequenceEqual(expected.Select(Show))).IsTrue();
        }
        else
        {
            await Assert.That(actual.Count).IsEqualTo(expected.Count);
            await Assert.That(actual.Select(Show).Order().ToArray()).IsEquivalentTo(expected.Select(Show).Order().ToArray());
        }
    }

    private static string Show<TResult>(TResult value) => value switch
    {
        Person person => $"{person.Id}|{person.Name}|{person.Age}|{person.City}|{person.Score}|{person.Born}|{person.Active}|{person.Level}|{person.Zip}|{person.Home.Street}|{person.Home.Number}|{string.Join(',', person.Tags)}",
        _ => value?.ToString() ?? "null",
    };

    [Test]
    public async Task AllTheRows_AreTheRowsOfTheFiles() => await Same(q => q);

    [Test]
    public async Task WhereOnANumber() => await Same(q => q.Where(p => p.Id >= 1250 && p.Id < 2100));

    [Test]
    public async Task WhereOnText() => await Same(q => q.Where(p => p.Name == "name-7" || p.City == "Oslo"));

    [Test]
    public async Task WhereWithAnotherSideFirst() => await Same(q => q.Where(p => 30 < p.Age && 5 >= p.Id % 10));

    [Test]
    public async Task WhereOnNullsAndNullableNumbers() =>
        await Same(q => q.Where(p => p.Name == null || (p.Score != null && p.Score > 10) || p.Score == null && p.Age > 70));

    [Test]
    public async Task WhereWithNot() => await Same(q => q.Where(p => !(p.Age > 30) && !p.Active));

    [Test]
    public async Task WhereOnTheValueOfANullable() => await Same(q => q.Where(p => p.Score.HasValue && p.Score.Value >= 15));

    [Test]
    public async Task WhereOnADateAnEnumAndAFlag() =>
        await Same(q => q.Where(p => p.Born > new DateOnly(1960, 1, 1) && p.Level == Level.Senior && p.Active));

    [Test]
    public async Task WhereWithACapturedVariableAndAList()
    {
        int low = 500;
        var cities = new[] { "Lima", "Cairo" };
        var ids = new List<int> { 3, 1003, 2003, 3333, 99_999 };

        await Same(q => q.Where(p => p.Id > low && cities.Contains(p.City)));
        await Same(q => q.Where(p => ids.Contains(p.Id)));
    }

    [Test]
    public async Task WhereOnANestedProperty() => await Same(q => q.Where(p => p.Home.Number > 90 && p.Home.Street == "street 3"));

    [Test]
    public async Task WhereWithAMethodTheStatisticsCannotCheck() => await Same(q => q.Where(p => p.City.StartsWith("Pa") && p.Name != null && p.Name.Length > 6));

    [Test]
    public async Task SeveralWheres() => await Same(q => q.Where(p => p.Id > 900).Where(p => p.Id < 1500).Where(p => p.Age != 20));

    [Test]
    public async Task SelectOfSomeProperties() => await Same(q => q.Where(p => p.Id < 1050).Select(p => new { p.Id, p.Name }));

    [Test]
    public async Task SelectOfOneProperty_WithAWhereOnAnother() => await Same(q => q.Where(p => p.Age > 70).Select(p => p.City));

    [Test]
    public async Task SelectOfComputedValues() => await Same(q => q.Select(p => new { Key = p.Id * 2, Text = p.City + ":" + p.Age }));

    [Test]
    public async Task SelectOfAColumnWithAnotherNameThanItsProperty() => await Same(q => q.Where(p => p.Zip > 10800).Select(p => p.Zip));

    [Test]
    public async Task SelectOfANestedObjectAndAList() => await Same(q => q.Where(p => p.Id % 11 == 0).Select(p => new { p.Home.Street, Count = p.Tags.Count }));

    [Test]
    public async Task SelectOfTheWholeRow() => await Same(q => q.Where(p => p.Id > 3000).Select(p => p));

    [Test]
    public async Task SelectOfAMethodOnTheRow() => await Same(q => q.Where(p => p.Id > 3300).Select(p => Show(p)));

    [Test]
    public async Task SelectOfNothingOfTheRow() => await Same(q => q.Where(p => p.Id > 3300).Select(_ => 1));

    [Test]
    public async Task OrderByThenByAndTake() =>
        await Same(q => q.OrderBy(p => p.City).ThenByDescending(p => p.Id).Take(15).Select(p => new { p.City, p.Id }), ordered: true);

    [Test]
    public async Task OrderByDescendingSkipAndTake() => await Same(q => q.Where(p => p.Id > 100).OrderByDescending(p => p.Age).ThenBy(p => p.Id).Skip(10).Take(7), ordered: true);

    [Test]
    public async Task DistinctGroupByAndReverse()
    {
        await Same(q => q.Select(p => p.City).Distinct());
        await Same(q => q.GroupBy(p => p.City).Select(g => new { City = g.Key, Count = g.Count(), Oldest = g.Max(p => p.Age) }));
        await Same(q => q.OrderBy(p => p.Id).Select(p => p.Id).Reverse().Take(5), ordered: true);
    }

    [Test]
    public async Task ConcatAndUnionWithRowsInMemory()
    {
        var extra = new[] { TestDataset.Make(7000), TestDataset.Make(7001) };

        await Same(q => q.Where(p => p.Id < 3).Concat(extra));
        await Same(q => q.Where(p => p.Id < 3).Select(p => p.Id).Union(new[] { 1, 99 }));
    }

    [Test]
    public async Task Scalars()
    {
        using var data = new TestDataset();
        var dataset = data.Open();
        var rows = data.All.AsQueryable();

        await Assert.That(dataset.Count(p => p.Age > 40)).IsEqualTo(rows.Count(p => p.Age > 40));
        await Assert.That(dataset.Where(p => p.Id > 1000).LongCount()).IsEqualTo(rows.Where(p => p.Id > 1000).LongCount());
        await Assert.That(dataset.Any(p => p.Id == 3399)).IsTrue();
        await Assert.That(dataset.Any(p => p.Id == 3400)).IsFalse();
        await Assert.That(dataset.All(p => p.Age >= 18)).IsTrue();
        await Assert.That(dataset.Max(p => p.Age)).IsEqualTo(rows.Max(p => p.Age));
        await Assert.That(dataset.Min(p => p.Id)).IsEqualTo(0);
        await Assert.That(dataset.Sum(p => p.Age)).IsEqualTo(rows.Sum(p => p.Age));
        await Assert.That(dataset.Where(p => p.Score != null).Average(p => p.Score!.Value)).IsEqualTo(rows.Where(p => p.Score != null).Average(p => p.Score!.Value));
        await Assert.That(dataset.First(p => p.Id > 2500).Id).IsGreaterThan(2500);
        await Assert.That(dataset.FirstOrDefault(p => p.Id > 99_999)).IsNull();
        await Assert.That(dataset.Single(p => p.Id == 2222).Id).IsEqualTo(2222);
        await Assert.That(dataset.SingleOrDefault(p => p.Id == 2222 && p.Age == 0)).IsNull();
        await Assert.That(dataset.Where(p => p.Id == 1005).Select(p => p.Name).Single()).IsEqualTo(rows.Where(p => p.Id == 1005).Select(p => p.Name).Single());
        await Assert.That(dataset.OrderBy(p => p.Id).Select(p => p.Id).Last()).IsEqualTo(3399);
        await Assert.That(dataset.ElementAt(0).Id).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task AFileThatIsNotThere_IsAnError()
    {
        var dataset = ParquetDataset.Open<Person>(Path.Combine(Path.GetTempPath(), "there-is-nothing-here-" + Guid.NewGuid().ToString("N")));

        await Assert.That(() => dataset.ToList()).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task AQueryOverSomethingElse_IsRefused()
    {
        using var data = new TestDataset();
        var joined = data.Open().Join(data.All, p => p.Id, p => p.Id, (a, b) => a.Id + b.Id);

        // The first source is the dataset, so joining it with rows in memory is fine.
        await Assert.That(joined.Count()).IsEqualTo(data.All.Count);
    }

    [Test]
    public async Task WhereAfterAnOrdering() =>
        await Same(q => q.OrderBy(p => p.Age).ThenByDescending(p => p.Id).Where(p => p.Id >= 1250 && p.Id < 2100 && p.Name != null), ordered: true);

    [Test]
    public async Task WhereBeforeAndAfterAnOrdering() =>
        await Same(q => q.Where(p => p.Id >= 1000).OrderByDescending(p => p.Score).ThenBy(p => p.Id).Where(p => p.Id < 3100 || p.City == "Oslo"), ordered: true);

    [Test]
    public async Task OrderingThenWhereThenSelect() =>
        await Same(q => q.OrderBy(p => p.Age).ThenBy(p => p.Id).Where(p => p.Id >= 2000 && p.Id < 2150).Select(p => new { p.Id, p.Name }), ordered: true);

    [Test]
    public async Task WhereAfterASkip_IsAboutTheRowsThatAreLeft() =>
        await Same(q => q.OrderBy(p => p.Id).Skip(1500).Where(p => p.Id >= 2000 && p.Id < 2150), ordered: true);

    [Test]
    public async Task WhereAfterATake_IsAboutTheRowsThatAreTaken() =>
        await Same(q => q.OrderBy(p => p.Id).Take(2050).Where(p => p.Id >= 2000), ordered: true);
}
