using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Column statistics for a page or column chunk (thrift <c>Statistics</c>).</summary>
public sealed class Statistics : IThriftStruct<Statistics>
{
    /// <summary>Deprecated max (signed byte-wise order); prefer <see cref="MaxValue"/>.</summary>
    public byte[]? Max { get; set; }

    /// <summary>Deprecated min (signed byte-wise order); prefer <see cref="MinValue"/>.</summary>
    public byte[]? Min { get; set; }

    public long? NullCount { get; set; }

    public long? DistinctCount { get; set; }

    public byte[]? MaxValue { get; set; }

    public byte[]? MinValue { get; set; }

    public bool? IsMaxValueExact { get; set; }

    public bool? IsMinValueExact { get; set; }

    public long? NanCount { get; set; }

    static Statistics IThriftStruct<Statistics>.Read(ref ThriftCompactReader r)
    {
        var s = new Statistics();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Binary: s.Max = r.ReadBytes(); break;
                case 2 when type == CompactType.Binary: s.Min = r.ReadBytes(); break;
                case 3 when type == CompactType.I64: s.NullCount = r.ReadI64(); break;
                case 4 when type == CompactType.I64: s.DistinctCount = r.ReadI64(); break;
                case 5 when type == CompactType.Binary: s.MaxValue = r.ReadBytes(); break;
                case 6 when type == CompactType.Binary: s.MinValue = r.ReadBytes(); break;
                case 7 when type is CompactType.BoolTrue or CompactType.BoolFalse:
                    s.IsMaxValueExact = type == CompactType.BoolTrue;
                    break;
                case 8 when type is CompactType.BoolTrue or CompactType.BoolFalse:
                    s.IsMinValueExact = type == CompactType.BoolTrue;
                    break;
                case 9 when type == CompactType.I64: s.NanCount = r.ReadI64(); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return s;
    }

    void IThriftStruct<Statistics>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        if (Max is not null) w.WriteBinaryField(1, Max);
        if (Min is not null) w.WriteBinaryField(2, Min);
        if (NullCount is { } nullCount) w.WriteI64Field(3, nullCount);
        if (DistinctCount is { } distinctCount) w.WriteI64Field(4, distinctCount);
        if (MaxValue is not null) w.WriteBinaryField(5, MaxValue);
        if (MinValue is not null) w.WriteBinaryField(6, MinValue);
        if (IsMaxValueExact is { } maxExact) w.WriteBoolField(7, maxExact);
        if (IsMinValueExact is { } minExact) w.WriteBoolField(8, minExact);
        if (NanCount is { } nanCount) w.WriteI64Field(9, nanCount);
        w.EndStruct();
    }
}

/// <summary>Size statistics of a column chunk (thrift <c>SizeStatistics</c>).</summary>
public sealed class SizeStatistics : IThriftStruct<SizeStatistics>
{
    public long? UnencodedByteArrayDataBytes { get; set; }

    public List<long>? RepetitionLevelHistogram { get; set; }

    public List<long>? DefinitionLevelHistogram { get; set; }

    static SizeStatistics IThriftStruct<SizeStatistics>.Read(ref ThriftCompactReader r)
    {
        var s = new SizeStatistics();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I64: s.UnencodedByteArrayDataBytes = r.ReadI64(); break;
                case 2 when type == CompactType.List: s.RepetitionLevelHistogram = ThriftStruct.ReadI64List(ref r); break;
                case 3 when type == CompactType.List: s.DefinitionLevelHistogram = ThriftStruct.ReadI64List(ref r); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return s;
    }

    void IThriftStruct<SizeStatistics>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        if (UnencodedByteArrayDataBytes is { } bytes) w.WriteI64Field(1, bytes);
        if (RepetitionLevelHistogram is not null) ThriftStruct.WriteI64List(ref w, 2, RepetitionLevelHistogram);
        if (DefinitionLevelHistogram is not null) ThriftStruct.WriteI64List(ref w, 3, DefinitionLevelHistogram);
        w.EndStruct();
    }
}
