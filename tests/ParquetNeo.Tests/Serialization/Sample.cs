using ParquetNeo.Metadata;
using ParquetNeo.Serialization;

namespace ParquetNeo.Tests.Serialization;

/// <summary>A row type with a property of every type the serializer writes, required and optional. Mirrored in tests/fixtures/verify_serializer_file.py.</summary>
public sealed class Sample
{
    public bool Flag { get; set; }

    public sbyte Tiny { get; set; }

    public byte Byte { get; set; }

    public short Short { get; set; }

    public ushort UShort { get; set; }

    public int Int { get; set; }

    public uint UInt { get; set; }

    public long Long { get; set; }

    public ulong ULong { get; set; }

    public float Float { get; set; }

    public double Double { get; set; }

    public Half Half { get; set; }

    [ParquetDecimal(12, 2)]
    public decimal Money { get; set; }

    public decimal Precise { get; set; }

    public string Name { get; set; } = "";

    public byte[] Data { get; set; } = [];

    public Guid Id { get; set; }

    public DateTime When { get; set; }

    public DateTimeOffset Offset { get; set; }

    public DateOnly Day { get; set; }

    public TimeOnly Time { get; set; }

    public string? Nickname { get; set; }

    public int? OptionalInt { get; set; }

    public double? OptionalDouble { get; set; }

    [ParquetDecimal(12, 2)]
    public decimal? OptionalMoney { get; set; }

    public DateTime? OptionalWhen { get; set; }

    public Guid? OptionalId { get; set; }

    public static readonly string[] Columns =
    [
        nameof(Flag), nameof(Tiny), nameof(Byte), nameof(Short), nameof(UShort), nameof(Int), nameof(UInt), nameof(Long), nameof(ULong),
        nameof(Float), nameof(Double), nameof(Half), nameof(Money), nameof(Precise), nameof(Name), nameof(Data), nameof(Id), nameof(When),
        nameof(Offset), nameof(Day), nameof(Time), nameof(Nickname), nameof(OptionalInt), nameof(OptionalDouble), nameof(OptionalMoney),
        nameof(OptionalWhen), nameof(OptionalId),
    ];

    /// <summary>Row <paramref name="i"/>: every value is a formula of it, so a reader can recompute the expectation.</summary>
    public static Sample Row(int i) => new()
    {
        Flag = i % 2 == 0,
        Tiny = (sbyte)(i % 200 - 100),
        Byte = (byte)(i * 5 % 256),
        Short = (short)(i * 1000 % 60000 - 30000),
        UShort = (ushort)(i * 1500 % 65536),
        Int = i * 100_000 - 2_000_000,
        UInt = (uint)(i * 100_000_000L % 4_000_000_000L),
        Long = i * 1_000_000_007L - 20_000_000_000L,
        ULong = (ulong)i * 400_000_000_000_000_000UL % 16_000_000_000_000_000_000UL,
        Float = i * 0.5f - 10,
        Double = i * 0.25 - 5,
        Half = (Half)(i * 0.5),
        Money = i * 12345.67m - 100_000m,
        Precise = i * 0.125m,
        Name = $"name {i}",
        Data = [(byte)i, (byte)(i + 1)],
        Id = new Guid(i, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8),
        When = DateTime.UnixEpoch.AddDays(i).AddSeconds(i).AddTicks(i * 10L),
        Offset = new DateTimeOffset(DateTime.UnixEpoch.AddHours(i).AddTicks(i * 10L), TimeSpan.Zero),
        Day = new DateOnly(2000, 1, 1).AddDays(i * 37),
        Time = new TimeOnly(0).Add(TimeSpan.FromTicks(i * 12_345_670L)),
        Nickname = i % 3 == 0 ? null : $"nick{i}",
        OptionalInt = i % 4 == 1 ? null : i,
        OptionalDouble = i % 5 == 2 ? null : i / 4.0,
        OptionalMoney = i % 6 == 3 ? null : i * 1.5m,
        OptionalWhen = i % 7 == 4 ? null : DateTime.UnixEpoch.AddMinutes(i),
        OptionalId = i % 8 == 5 ? null : new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
    };

    public static List<Sample> Rows(int count) => Enumerable.Range(0, count).Select(Row).ToList();

    /// <summary>The expected value of a column in row <paramref name="i"/>, as the typed reader gives it (null for null).</summary>
    public static object? Expected(string column, int i)
    {
        var row = Row(i);
        return column switch
        {
            nameof(Flag) => row.Flag,
            nameof(Tiny) => row.Tiny,
            nameof(Byte) => row.Byte,
            nameof(Short) => row.Short,
            nameof(UShort) => row.UShort,
            nameof(Int) => row.Int,
            nameof(UInt) => row.UInt,
            nameof(Long) => row.Long,
            nameof(ULong) => row.ULong,
            nameof(Float) => row.Float,
            nameof(Double) => row.Double,
            nameof(Half) => row.Half,
            nameof(Money) => row.Money,
            nameof(Precise) => row.Precise,
            nameof(Name) => row.Name,
            nameof(Data) => row.Data,
            nameof(Id) => row.Id,
            nameof(When) => DateTime.SpecifyKind(row.When, DateTimeKind.Utc),
            nameof(Offset) => row.Offset.UtcDateTime,
            nameof(Day) => row.Day,
            nameof(Time) => row.Time,
            nameof(Nickname) => row.Nickname,
            nameof(OptionalInt) => row.OptionalInt,
            nameof(OptionalDouble) => row.OptionalDouble,
            nameof(OptionalMoney) => row.OptionalMoney,
            nameof(OptionalWhen) => row.OptionalWhen is { } when ? DateTime.SpecifyKind(when, DateTimeKind.Utc) : null,
            nameof(OptionalId) => row.OptionalId,
            _ => throw new ArgumentException(column),
        };
    }
}
