using ParquetNeo.DataModel;

namespace ParquetNeo.IntegrationTests;

/// <summary>A row of every simple type: the same columns as <c>basic_table</c> of <c>generate.py</c>.</summary>
public sealed class BasicRow
{
    public long Id { get; set; }

    public sbyte I8 { get; set; }

    public short I16 { get; set; }

    public int? I32 { get; set; }

    public byte U8 { get; set; }

    public ushort U16 { get; set; }

    public uint U32 { get; set; }

    public ulong U64 { get; set; }

    public float? F32 { get; set; }

    public double? F64 { get; set; }

    public bool? B { get; set; }

    public string? S { get; set; }

    public byte[]? Bin { get; set; }

    public static BasicRow[] Many(int count) => [.. Enumerable.Range(0, count).Select(Make)];

    public static BasicRow Make(int i)
    {
        float f32 = i * 0.25f - 100f;
        double f64 = i * 0.1 - 50.0;
        (f32, f64) = i switch { 5 => (float.NaN, double.NaN), 6 => (float.PositiveInfinity, double.PositiveInfinity), 7 => (float.NegativeInfinity, double.NegativeInfinity), _ => (f32, f64) };
        return new BasicRow
        {
            Id = i,
            I8 = (sbyte)(((i * 7) % 256) - 128),
            I16 = (short)(((i * 131) % 65536) - 32768),
            I32 = i % 7 == 0 ? null : (i * 997) - 2_000_000,
            U8 = (byte)((i * 13) % 256),
            U16 = (ushort)((i * 257) % 65536),
            U32 = (uint)((i * 2654435761L) % 4294967296L),
            U64 = unchecked((ulong)i * 0x9E3779B97F4A7C15UL),
            F32 = i % 11 == 0 ? null : f32,
            F64 = i % 13 == 0 ? null : f64,
            B = i % 10 == 0 ? null : i % 3 == 0,
            S = i % 13 == 0 ? null : i % 500 == 499 ? new string('x', 5000) : i % 17 == 0 ? "" : $"row {i} é€ ü 😀",
            Bin = i % 15 == 0 ? null : Enumerable.Repeat((byte)(i % 256), i % 9).ToArray(),
        };
    }
}

/// <summary>A row of the logical types: dates, times, moments, decimals, a Guid, a half precision number.</summary>
public sealed class LogicalRow
{
    private static readonly DateTime Epoch = new(1970, 1, 1);

    public int Id { get; set; }

    public DateOnly Date { get; set; }

    [ParquetTime(TimeUnit.Millis)]
    public TimeOnly TimeMillis { get; set; }

    [ParquetTime(TimeUnit.Micros)]
    public TimeOnly TimeMicros { get; set; }

    [ParquetTimestamp(TimeUnit.Millis, IsAdjustedToUtc = false)]
    public DateTime? TsMillis { get; set; }

    [ParquetTimestamp(TimeUnit.Micros, IsAdjustedToUtc = true)]
    public DateTime TsMicrosUtc { get; set; }

    [ParquetTimestamp(TimeUnit.Nanos, IsAdjustedToUtc = true)]
    public DateTime TsNanosUtc { get; set; }

    [ParquetDecimal(9, 2)]
    public decimal? Dec9 { get; set; }

    [ParquetDecimal(18, 4)]
    public decimal Dec18 { get; set; }

    [ParquetDecimal(30, 6)]
    public decimal Dec30 { get; set; }

    public Guid Guid { get; set; }

    public Half Half { get; set; }

    public static LogicalRow[] Many(int count) => [.. Enumerable.Range(0, count).Select(i => new LogicalRow
    {
        Id = i,
        Date = new DateOnly(1970, 1, 1).AddDays((i * 3) - 1000),
        TimeMillis = new TimeOnly(TimeSpan.FromMilliseconds((i * 86399L) % 86_400_000).Ticks),
        TimeMicros = new TimeOnly(((i * 1_234_567L) % 86_400_000_000) * 10),
        TsMillis = i % 5 == 0 ? null : Epoch.AddTicks((1_700_000_000_000 + (i * 1000L)) * TimeSpan.TicksPerMillisecond),
        TsMicrosUtc = DateTime.SpecifyKind(Epoch.AddTicks((1_700_000_000_000_000 + (i * 1_000_001L)) * 10), DateTimeKind.Utc),
        TsNanosUtc = DateTime.SpecifyKind(Epoch.AddTicks((1_700_000_000_000_000_000L / 100) + i), DateTimeKind.Utc),
        Dec9 = i % 9 == 0 ? null : (i % 3 == 0 ? -1 : 1) * ((i * 7919L) % 10_000_000) / 100m,
        Dec18 = (i % 3 == 0 ? -1 : 1) * ((i * 7919L) % 1_000_000_000_000L) / 10_000m,
        Dec30 = (i % 3 == 0 ? -1 : 1) * ((i * 7919L) % 1_000_000_000_000_000L) / 1_000_000m,
        Guid = new Guid(i, (short)(i % 1000), (short)(i % 77), (byte)i, 2, 3, 4, 5, 6, 7, (byte)(i >> 8)),
        Half = (Half)((i % 100) / 4.0),
    })];
}

public sealed class ListsRow
{
    public int Id { get; set; }

    public List<string?>? Tags { get; set; }

    public List<int?>? Nums { get; set; }
}

public sealed class StructRow
{
    public int Id { get; set; }

    public PersonRow? Person { get; set; }
}

public sealed class PersonRow
{
    public string? Name { get; set; }

    public int? Age { get; set; }

    public HomeRow? Home { get; set; }
}

public sealed class HomeRow
{
    public string? City { get; set; }

    public int? Zip { get; set; }
}

public sealed class MapRow
{
    public int Id { get; set; }

    public Dictionary<string, int?>? Scores { get; set; }
}

public sealed class ListStructRow
{
    public int Id { get; set; }

    public List<ItemRow?>? Items { get; set; }
}

public sealed class ItemRow
{
    public string? Sku { get; set; }

    public int? Qty { get; set; }
}

/// <summary>The nested rows the tests write, with nulls and empty collections at every level.</summary>
internal static class NestedRows
{
    public static ListsRow[] Lists(int n) => [.. Enumerable.Range(0, n).Select(i => new ListsRow
    {
        Id = i,
        Tags = i % 11 == 0 ? null : i % 7 == 0 ? [] : [.. Enumerable.Range(0, (i % 4) + 1).Select(j => (i + j) % 5 == 0 ? null : $"t{(i + j) % 9}")],
        Nums = i % 13 == 0 ? null : [.. Enumerable.Range(0, i % 5).Select(j => (i + j) % 6 == 0 ? (int?)null : i + j)],
    })];

    public static StructRow[] Structs(int n) => [.. Enumerable.Range(0, n).Select(i => new StructRow
    {
        Id = i,
        Person = i % 9 == 0 ? null : new PersonRow
        {
            Name = $"p{i}",
            Age = i % 8 == 0 ? null : 20 + (i % 50),
            Home = i % 4 == 0 ? null : new HomeRow { City = i % 10 == 1 ? null : $"city {i % 20}", Zip = i % 3 == 0 ? null : i },
        },
    })];

    public static MapRow[] Maps(int n) => [.. Enumerable.Range(0, n).Select(i => new MapRow
    {
        Id = i,
        Scores = i % 10 == 0 ? null : Enumerable.Range(0, i % 4).ToDictionary(j => $"k{(i + j) % 6}", j => (i + j) % 7 == 0 ? (int?)null : i * j),
    })];

    public static ListStructRow[] ListsOfStructs(int n) => [.. Enumerable.Range(0, n).Select(i => new ListStructRow
    {
        Id = i,
        Items = i % 12 == 0 ? null : [.. Enumerable.Range(0, i % 4).Select(j => (i + j) % 8 == 0 ? null : new ItemRow { Sku = $"s{i + j}", Qty = j % 3 == 2 ? null : (i % 7) + j })],
    })];
}
