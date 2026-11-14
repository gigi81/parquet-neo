using ParquetNeo.Serialization;

namespace ParquetNeo.Linq.Tests;

/// <summary>Which files make up a dataset, and what reading them in parallel does.</summary>
public class DirectoryTests
{
    private static void Write(string path, IEnumerable<Person> rows)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        ParquetSerializer.Serialize(rows.ToList(), stream, new ParquetSerializerOptions { RowGroupSize = 50 });
    }

    [Test]
    public async Task AFileIsADatasetOfItsOwn()
    {
        using var data = new TestDataset();

        var rows = ParquetDataset.Open<Person>(Path.Combine(data.Directory, "people-2.parquet")).Select(p => p.Id).ToList();

        await Assert.That(rows.Count).IsEqualTo(400);
        await Assert.That(rows.Min()).IsEqualTo(2000);
    }

    [Test]
    public async Task FoldersBelowTheDirectoryAreSearched_UnlessTheOptionSaysNot()
    {
        using var data = new TestDataset();
        Write(Path.Combine(data.Directory, "year=2025", "month=01", "more.parquet"), Enumerable.Range(9000, 30).Select(TestDataset.Make));

        var all = data.Open();
        var top = data.Open(new ParquetDatasetOptions { Recursive = false });

        await Assert.That(all.Count()).IsEqualTo(1630);
        await Assert.That(top.Count()).IsEqualTo(1600);
        await Assert.That(all.Count(p => p.Id >= 9000)).IsEqualTo(30);
    }

    [Test]
    public async Task OnlyTheFilesThePatternMatchesAreRead()
    {
        using var data = new TestDataset();
        Write(Path.Combine(data.Directory, "other.dat"), Enumerable.Range(9000, 30).Select(TestDataset.Make));

        await Assert.That(data.Open().Count()).IsEqualTo(1600);
        await Assert.That(data.Open(new ParquetDatasetOptions { SearchPattern = "*.dat" }).Count()).IsEqualTo(30);
        await Assert.That(data.Open(new ParquetDatasetOptions { SearchPattern = "people-1.parquet" }).Count()).IsEqualTo(400);
    }

    [Test]
    public async Task OneFileAtATime_TheRowsComeInTheOrderOfTheNames()
    {
        using var data = new TestDataset();

        var ids = data.Open(new ParquetDatasetOptions { MaxDegreeOfParallelism = 1 }).Select(p => p.Id).ToList();

        await Assert.That(ids.SequenceEqual(data.All.Select(p => p.Id))).IsTrue();
    }

    [Test]
    public async Task ManyFilesAtATime_GiveTheSameRows()
    {
        using var data = new TestDataset();

        var parallel = data.Open(new ParquetDatasetOptions { MaxDegreeOfParallelism = 4 }).Select(p => p.Id).ToList();

        await Assert.That(parallel.Order().SequenceEqual(data.All.Select(p => p.Id).Order())).IsTrue();
    }

    [Test]
    public async Task TakeAndFirst_StopReadingBeforeTheEnd()
    {
        using var data = new TestDataset();

        await Assert.That(data.Open().Take(7).Count()).IsEqualTo(7);
        await Assert.That(data.Open().Where(p => p.Id >= 3000).Select(p => p.Id).Take(5).ToList().All(id => id >= 3000)).IsTrue();
        await Assert.That(data.Open().First(p => p.Id == 1234).Id).IsEqualTo(1234);
    }

    [Test]
    public async Task AFileThatIsNotParquet_IsAnErrorWhoseTypeIsKept_WhetherFilesAreReadOneAtATimeOrNot()
    {
        using var data = new TestDataset();
        File.WriteAllText(Path.Combine(data.Directory, "people-9.parquet"), "this is not a Parquet file at all, only text, long enough");

        await Assert.That(() => data.Open().ToList()).Throws<ParquetNeo.Exceptions.ParquetException>();
        await Assert.That(() => data.Open(new ParquetDatasetOptions { MaxDegreeOfParallelism = 1 }).ToList()).Throws<ParquetNeo.Exceptions.ParquetException>();
    }

    [Test]
    public async Task AFileWithoutAColumnTheRowTypeCanDoWithout_GivesNulls()
    {
        using var data = new TestDataset();
        var path = Path.Combine(data.Directory, "narrow", "narrow.parquet");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        {
            ParquetSerializer.Serialize(new[] { new Narrow { Id = 5000 } }, stream);
        }

        var rows = data.Open().Where(p => p.Id == 5000).Select(p => new { p.Id, p.Name, p.Score }).ToList();

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsNull();
    }

    public sealed class Narrow
    {
        public int Id { get; set; }

        public string City { get; set; } = "";

        public int Age { get; set; }

        public string? Name { get; set; }

        public double? Score { get; set; }
    }
}
