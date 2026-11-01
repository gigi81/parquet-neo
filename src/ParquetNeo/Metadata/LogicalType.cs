using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Kind of a <see cref="LogicalType"/>. Values are the field ids of the thrift union.</summary>
public enum LogicalTypeKind
{
    /// <summary>A union member this library does not know; callers should fall back to the physical type.</summary>
    Unrecognized = 0,
    String = 1,
    Map = 2,
    List = 3,
    Enum = 4,
    Decimal = 5,
    Date = 6,
    Time = 7,
    Timestamp = 8,
    Integer = 10,
    /// <summary>A column that only contains nulls (thrift <c>UNKNOWN</c> / <c>NullType</c>).</summary>
    Null = 11,
    Json = 12,
    Bson = 13,
    Uuid = 14,
    Float16 = 15,
}

/// <summary>
/// A logical type annotation (thrift <c>LogicalType</c> union). Only the parameters relevant to
/// <see cref="Kind"/> are meaningful.
/// </summary>
public sealed record LogicalType
{
    public static readonly LogicalType String = new(LogicalTypeKind.String);
    public static readonly LogicalType Map = new(LogicalTypeKind.Map);
    public static readonly LogicalType List = new(LogicalTypeKind.List);
    public static readonly LogicalType Enum = new(LogicalTypeKind.Enum);
    public static readonly LogicalType Date = new(LogicalTypeKind.Date);
    public static readonly LogicalType Null = new(LogicalTypeKind.Null);
    public static readonly LogicalType Json = new(LogicalTypeKind.Json);
    public static readonly LogicalType Bson = new(LogicalTypeKind.Bson);
    public static readonly LogicalType Uuid = new(LogicalTypeKind.Uuid);
    public static readonly LogicalType Float16 = new(LogicalTypeKind.Float16);
    public static readonly LogicalType Unrecognized = new(LogicalTypeKind.Unrecognized);

    private LogicalType(LogicalTypeKind kind)
    {
        Kind = kind;
    }

    public LogicalTypeKind Kind { get; private init; }

    /// <summary>Decimal scale (<see cref="LogicalTypeKind.Decimal"/>).</summary>
    public int Scale { get; private init; }

    /// <summary>Decimal precision (<see cref="LogicalTypeKind.Decimal"/>).</summary>
    public int Precision { get; private init; }

    /// <summary>Whether the value is normalized to UTC (<see cref="LogicalTypeKind.Time"/>, <see cref="LogicalTypeKind.Timestamp"/>).</summary>
    public bool IsAdjustedToUtc { get; private init; }

    /// <summary>Resolution (<see cref="LogicalTypeKind.Time"/>, <see cref="LogicalTypeKind.Timestamp"/>).</summary>
    public TimeUnit Unit { get; private init; }

    /// <summary>Integer width in bits: 8, 16, 32 or 64 (<see cref="LogicalTypeKind.Integer"/>).</summary>
    public int BitWidth { get; private init; }

    public bool IsSigned { get; private init; }

    public static LogicalType Decimal(int scale, int precision) =>
        new(LogicalTypeKind.Decimal) { Scale = scale, Precision = precision };

    public static LogicalType Time(bool isAdjustedToUtc, TimeUnit unit) =>
        new(LogicalTypeKind.Time) { IsAdjustedToUtc = isAdjustedToUtc, Unit = unit };

    public static LogicalType Timestamp(bool isAdjustedToUtc, TimeUnit unit) =>
        new(LogicalTypeKind.Timestamp) { IsAdjustedToUtc = isAdjustedToUtc, Unit = unit };

    public static LogicalType Integer(int bitWidth, bool isSigned) =>
        new(LogicalTypeKind.Integer) { BitWidth = bitWidth, IsSigned = isSigned };

    internal static LogicalType Read(ref ThriftCompactReader reader)
    {
        var result = Unrecognized;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            if (type != CompactType.Struct)
            {
                reader.Skip(type);
                continue;
            }

            switch ((LogicalTypeKind)id)
            {
                case LogicalTypeKind.String: result = String; reader.Skip(type); break;
                case LogicalTypeKind.Map: result = Map; reader.Skip(type); break;
                case LogicalTypeKind.List: result = List; reader.Skip(type); break;
                case LogicalTypeKind.Enum: result = Enum; reader.Skip(type); break;
                case LogicalTypeKind.Date: result = Date; reader.Skip(type); break;
                case LogicalTypeKind.Null: result = Null; reader.Skip(type); break;
                case LogicalTypeKind.Json: result = Json; reader.Skip(type); break;
                case LogicalTypeKind.Bson: result = Bson; reader.Skip(type); break;
                case LogicalTypeKind.Uuid: result = Uuid; reader.Skip(type); break;
                case LogicalTypeKind.Float16: result = Float16; reader.Skip(type); break;
                case LogicalTypeKind.Decimal: result = ReadDecimal(ref reader); break;
                case LogicalTypeKind.Time: result = ReadTime(ref reader, isTimestamp: false); break;
                case LogicalTypeKind.Timestamp: result = ReadTime(ref reader, isTimestamp: true); break;
                case LogicalTypeKind.Integer: result = ReadInteger(ref reader); break;
                default: reader.Skip(type); break; // e.g. VARIANT, GEOMETRY: not supported yet
            }
        }

        reader.EndStruct();
        return result;
    }

    /// <summary>
    /// Writes the union's single member inside an already-open struct. Unrecognized annotations cannot
    /// be written; callers omit the field instead.
    /// </summary>
    internal void WriteMembers(ref ThriftCompactWriter writer)
    {
        writer.BeginStructField((short)Kind);
        switch (Kind)
        {
            case LogicalTypeKind.Decimal:
                writer.WriteI32Field(1, Scale);
                writer.WriteI32Field(2, Precision);
                break;
            case LogicalTypeKind.Time:
            case LogicalTypeKind.Timestamp:
                writer.WriteBoolField(1, IsAdjustedToUtc);
                writer.BeginStructField(2); // TimeUnit union
                writer.BeginStructField((short)Unit);
                writer.EndStruct();
                writer.EndStruct();
                break;
            case LogicalTypeKind.Integer:
                writer.WriteI8Field(1, (sbyte)BitWidth);
                writer.WriteBoolField(2, IsSigned);
                break;
        }

        writer.EndStruct();
    }

    private static LogicalType ReadDecimal(ref ThriftCompactReader reader)
    {
        int scale = 0, precision = 0;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: scale = reader.ReadI32(); break;
                case 2 when type == CompactType.I32: precision = reader.ReadI32(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return Decimal(scale, precision);
    }

    private static LogicalType ReadTime(ref ThriftCompactReader reader, bool isTimestamp)
    {
        bool adjusted = false;
        var unit = TimeUnit.Millis;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type is CompactType.BoolTrue or CompactType.BoolFalse:
                    adjusted = type == CompactType.BoolTrue;
                    break;
                case 2 when type == CompactType.Struct:
                    unit = ReadTimeUnit(ref reader);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.EndStruct();
        return isTimestamp ? Timestamp(adjusted, unit) : Time(adjusted, unit);
    }

    private static TimeUnit ReadTimeUnit(ref ThriftCompactReader reader)
    {
        var unit = TimeUnit.Millis;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            if (id is >= 1 and <= 3 && type == CompactType.Struct)
            {
                unit = (TimeUnit)id;
            }

            reader.Skip(type);
        }

        reader.EndStruct();
        return unit;
    }

    private static LogicalType ReadInteger(ref ThriftCompactReader reader)
    {
        int bitWidth = 0;
        bool isSigned = false;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I8: bitWidth = reader.ReadI8(); break;
                case 2 when type is CompactType.BoolTrue or CompactType.BoolFalse:
                    isSigned = type == CompactType.BoolTrue;
                    break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return Integer(bitWidth, isSigned);
    }
}
