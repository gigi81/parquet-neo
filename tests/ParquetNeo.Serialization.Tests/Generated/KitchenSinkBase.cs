using ParquetNeo.DataModel;
using ParquetNeo.Tests;

namespace ParquetNeo.Serialization.Tests.Generated;

/// <summary>
/// Every kind of column a flat row can have, and the attributes that adjust them. <see cref="KitchenSink"/> gets a generated mapping
/// and <see cref="KitchenSinkTwin"/> is left to reflection, so the two can be compared.
/// </summary>
public abstract class KitchenSinkBase
{
    public bool Flag { get; set; }

    public byte Byte { get; set; }

    public sbyte SignedByte { get; set; }

    public short Short { get; set; }

    public ushort UnsignedShort { get; set; }

    public int Int { get; set; }

    public uint UnsignedInt { get; set; }

    public long Long { get; set; }

    public ulong UnsignedLong { get; set; }

    public float Float { get; set; }

    public double Double { get; set; }

    [ParquetDecimal(18, 4)]
    public decimal Money { get; set; }

    public string Text { get; set; } = "";

    public string? Note { get; set; }

    [ParquetTimestamp(TimeUnit.Millis)]
    public DateTime Moment { get; set; }

    public DateTimeOffset Offset { get; set; }

    public DateOnly Day { get; set; }

    [ParquetTime(TimeUnit.Nanos)]
    public TimeOnly Clock { get; set; }

    public Guid Id { get; set; }

    public Half Small { get; set; }

    public byte[] Payload { get; set; } = [];

    public byte[]? Blob { get; set; }

    public DayOfWeek Weekday { get; set; }

    public DayOfWeek? MaybeWeekday { get; set; }

    public int? MaybeInt { get; set; }

    [ParquetDecimal(10, 2)]
    public decimal? MaybeMoney { get; set; }

    public DateTime? MaybeMoment { get; set; }

    public Guid? MaybeId { get; set; }

    [ParquetColumn("renamed", Order = -1)]
    public int Renamed { get; set; }

    [ParquetIgnore]
    public int Ignored { get; set; }

    [ParquetEncoding(ParquetEncoding.DeltaBinaryPacked)]
    public long Counter { get; set; }

    [ParquetDictionary(DictionaryMode.Disabled)]
    public string? Unique { get; set; }

    [ParquetDictionary(DictionaryMode.Adaptive, SizeLimit = 1024)]
    public string Category { get; set; } = "";

    /// <summary>Written, but there is nothing to read it into.</summary>
    public int Derived => Int * 2;
}
