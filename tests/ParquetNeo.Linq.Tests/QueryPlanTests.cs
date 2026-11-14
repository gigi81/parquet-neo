namespace ParquetNeo.Linq.Tests;

/// <summary>What queries read of the dataset: which files and row groups are skipped, and which columns are read.</summary>
public class QueryPlanTests
{
    [Test]
    public async Task AQueryWithNoCondition_ReadsEverything()
    {
        using var data = new TestDataset();

        var plan = data.Open().Explain();

        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(0);
        await Assert.That(plan.RowGroups).IsEqualTo(16);
        await Assert.That(plan.RowGroupsSkipped).IsEqualTo(0);
        await Assert.That(plan.Filter).IsNull();
        await Assert.That(plan.Columns).IsNull();
        await Assert.That(plan.AnsweredFromMetadata).IsFalse();
    }

    [Test]
    public async Task AConditionOnIds_SkipsTheFilesAndRowGroupsOutsideTheRange()
    {
        using var data = new TestDataset();

        // File 2 has 2000 to 2399 in four row groups of 100: the range keeps the first two.
        var plan = data.Open().Where(p => p.Id >= 2000 && p.Id < 2150).Explain();

        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(plan.RowGroups).IsEqualTo(4);
        await Assert.That(plan.RowGroupsSkipped).IsEqualTo(2);
        await Assert.That(plan.Filter).IsEqualTo("(Id >= 2000 and Id < 2150)");
    }

    [Test]
    public async Task ARangeNoRowIsIn_SkipsEveryFile()
    {
        using var data = new TestDataset();

        var plan = data.Open().Where(p => p.Id > 400 && p.Id < 1000).Explain();

        await Assert.That(plan.FilesSkipped).IsEqualTo(4);
        await Assert.That(plan.RowGroups).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTheColumnsAQueryUsesAreRead_WhenItEndsInASelect()
    {
        using var data = new TestDataset();

        var plan = data.Open().Where(p => p.Age > 70).Select(p => new { p.Id, p.Zip }).Explain();

        await Assert.That(plan.Columns!).IsEquivalentTo(new[] { "Age", "Id", "home_zip" }.Order().ToArray());
    }

    [Test]
    public async Task WholeRowsAreRead_WhenTheQueryDoesNotEndInASelect()
    {
        using var data = new TestDataset();

        await Assert.That(data.Open().Where(p => p.Age > 70).Explain().Columns).IsNull();
        await Assert.That(data.Open().Where(p => p.Age > 70).Select(p => p).Explain().Columns).IsNull();
        await Assert.That(data.Open().Select(p => p.ToString()).Explain().Columns).IsNull();
        await Assert.That(data.Open().GroupBy(p => p.City).Select(g => g.Key).Explain().Columns).IsNull();
    }

    [Test]
    public async Task OrderingAndPaging_BeforeTheSelect_UseTheirColumnsToo()
    {
        using var data = new TestDataset();

        var plan = data.Open().OrderBy(p => p.City).ThenBy(p => p.Born).Skip(5).Take(10).Select(p => p.Name).Explain();

        await Assert.That(plan.Columns!.Order().ToArray()).IsEquivalentTo(new[] { "Born", "City", "Name" });
    }

    [Test]
    public async Task TheNumberOfRowsAndWhetherThereAreAny_AreAnsweredFromTheFooters()
    {
        using var data = new TestDataset();
        var dataset = data.Open();

        await Assert.That(dataset.Explain().AnsweredFromMetadata).IsFalse();
        await Assert.That(dataset.Count()).IsEqualTo(1600);
        await Assert.That(dataset.LongCount()).IsEqualTo(1600L);
        await Assert.That(dataset.Any()).IsTrue();
        await Assert.That(dataset.Where(p => p.Id > 0).Any()).IsTrue();
    }

    [Test]
    public async Task AnEmptyDataset_HasNoRows()
    {
        using var data = new TestDataset();
        foreach (string file in Directory.GetFiles(data.Directory))
        {
            File.Delete(file);
        }

        var dataset = data.Open();

        await Assert.That(dataset.Count()).IsEqualTo(0);
        await Assert.That(dataset.Any()).IsFalse();
        await Assert.That(dataset.ToList()).IsEmpty();
        await Assert.That(dataset.Where(p => p.Id > 1).Select(p => p.Name).ToList()).IsEmpty();
    }

    [Test]
    [Arguments("p => p.Age > 30 && p.City.StartsWith(\"Pa\")", "Age > 30")]
    [Arguments("p => p.City.StartsWith(\"Pa\")", null)]
    [Arguments("p => p.Age > 30 || p.City.StartsWith(\"Pa\")", null)]
    [Arguments("p => 30 < p.Age", "Age > 30")]
    [Arguments("p => p.Name == null", "Name is null")]
    [Arguments("p => p.Name != null", "Name is not null")]
    [Arguments("p => p.Score == null || p.Score > 3.0", "(Score is null or Score > 3)")]
    [Arguments("p => !(p.Age > 30)", "not Age > 30")]
    [Arguments("p => !(p.Age > 30 && p.City.StartsWith(\"Pa\"))", null)]
    [Arguments("p => p.Active", "Active == True")]
    [Arguments("p => !p.Active", "not Active == True")]
    [Arguments("p => p.Score.HasValue", "Score is not null")]
    [Arguments("p => p.Home.Number > 4", "Home.Number > 4")]
    [Arguments("p => p.Zip == 10005", "home_zip == 10005")]
    [Arguments("p => p.Level == Level.Senior", "Level == Senior")]
    [Arguments("p => p.Age > p.Id", null)]
    [Arguments("p => p.Age + 1 > 30", null)]
    public async Task TheConditionTakenFromAPredicate_IsWhatTheStatisticsCanCheck(string predicate, string? expected)
    {
        using var data = new TestDataset();
        var query = predicate switch
        {
            "p => p.Age > 30 && p.City.StartsWith(\"Pa\")" => data.Open().Where(p => p.Age > 30 && p.City.StartsWith("Pa")),
            "p => p.City.StartsWith(\"Pa\")" => data.Open().Where(p => p.City.StartsWith("Pa")),
            "p => p.Age > 30 || p.City.StartsWith(\"Pa\")" => data.Open().Where(p => p.Age > 30 || p.City.StartsWith("Pa")),
            "p => 30 < p.Age" => data.Open().Where(p => 30 < p.Age),
            "p => p.Name == null" => data.Open().Where(p => p.Name == null),
            "p => p.Name != null" => data.Open().Where(p => p.Name != null),
            "p => p.Score == null || p.Score > 3.0" => data.Open().Where(p => p.Score == null || p.Score > 3.0),
            "p => !(p.Age > 30)" => data.Open().Where(p => !(p.Age > 30)),
            "p => !(p.Age > 30 && p.City.StartsWith(\"Pa\"))" => data.Open().Where(p => !(p.Age > 30 && p.City.StartsWith("Pa"))),
            "p => p.Active" => data.Open().Where(p => p.Active),
            "p => !p.Active" => data.Open().Where(p => !p.Active),
            "p => p.Score.HasValue" => data.Open().Where(p => p.Score.HasValue),
            "p => p.Home.Number > 4" => data.Open().Where(p => p.Home.Number > 4),
            "p => p.Zip == 10005" => data.Open().Where(p => p.Zip == 10005),
            "p => p.Level == Level.Senior" => data.Open().Where(p => p.Level == Level.Senior),
            "p => p.Age > p.Id" => data.Open().Where(p => p.Age > p.Id),
            _ => data.Open().Where(p => p.Age + 1 > 30),
        };

        await Assert.That(query.Explain().Filter).IsEqualTo(expected);
    }

    [Test]
    public async Task ACapturedValueAndAListOfValues_AreReadWhenTheQueryRuns()
    {
        using var data = new TestDataset();
        int low = 3200;
        var ids = new[] { 5, 2005 };
        var above = data.Open().Where(p => p.Id >= low);
        var among = data.Open().Where(p => ids.Contains(p.Id));

        await Assert.That(above.Explain().FilesSkipped).IsEqualTo(3);
        await Assert.That(among.Explain().FilesSkipped).IsEqualTo(2);
        low = 0;
        await Assert.That(above.Explain().FilesSkipped).IsEqualTo(0);
    }

    [Test]
    public async Task ExplainingAQueryThatIsNotOfADataset_IsAMistake()
    {
        await Assert.That(() => new[] { 1, 2 }.AsQueryable().Explain()).Throws<ArgumentException>();
    }
}
