using ParquetNeo.Serialization;

namespace ParquetNeo.Linq.Tests;

/// <summary>A directory of four files of 400 people each in four row groups of 100, ids in order: file 0 has 0 to 399, file 1 has 1000 to 1399, and so on.</summary>
internal sealed class TestDataset : IDisposable
{
    public const int Files = 4;
    public const int RowsPerFile = 400;
    public const int RowGroupSize = 100;

    private static readonly string[] Cities = ["Rome", "Paris", "Oslo", "Lima", "Cairo"];

    public TestDataset(ParquetWriterOptions? writer = null)
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parquet-neo-linq-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        for (int file = 0; file < Files; file++)
        {
            var rows = Enumerable.Range(0, RowsPerFile).Select(i => Make(file * 1000 + i)).ToList();
            All.AddRange(rows);
            using var stream = File.Create(System.IO.Path.Combine(Directory, $"people-{file}.parquet"));
            ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = RowGroupSize, Writer = writer ?? new ParquetWriterOptions() });
        }
    }

    public string Directory { get; }

    /// <summary>Every row of every file.</summary>
    public List<Person> All { get; } = [];

    public ParquetDataset<Person> Open(ParquetDatasetOptions? options = null) => ParquetDataset.Open<Person>(Directory, options);

    public static Person Make(int id) => new()
    {
        Id = id,
        Name = id % 7 == 0 ? null : $"name-{id % 50}",
        Age = 18 + (id % 60),
        City = Cities[(id / 100) % Cities.Length],
        Score = id % 5 == 0 ? null : id % 13 * 1.5,
        Born = new DateOnly(1950, 1, 1).AddDays(id * 3),
        Active = id % 3 == 0,
        Level = id % 4 == 0 ? Level.Senior : Level.Junior,
        Zip = 10000 + (id % 900),
        Home = new Address { Street = $"street {id % 20}", Number = id % 99 },
        Tags = id % 4 == 0 ? [] : [$"t{id % 3}", "x"],
    };

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still being deleted by the system: the temporary directory is left for the system to clean.
        }
    }
}
