using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Filtering;

/// <summary>
/// How the values of a column compare. The statistics of a column chunk are physical values (an <see cref="int"/> for a date, bytes
/// for a decimal), and a filter has .NET values (a <see cref="DateOnly"/>, a <see cref="decimal"/>). A domain turns both into keys of
/// one kind, in the way the column is read, so that a key that is less than another is a value that is, and says how keys are compared.
/// </summary>
internal abstract class ValueDomain
{
    /// <summary>Whether the column can hold a NaN, which is no bound and is not equal to anything.</summary>
    public virtual bool HasNotANumbers => false;

    /// <summary>The domain of the values of a column, or null when the statistics of the column say nothing a filter can use (an INT96).</summary>
    public static ValueDomain? For(PrimitiveNode column)
    {
        var logical = column.LogicalType;
        switch (column.Type)
        {
            case PhysicalType.Boolean:
                return new BooleanDomain();
            case PhysicalType.Int32:
            case PhysicalType.Int64:
                return ForInteger(logical);
            case PhysicalType.Float:
            case PhysicalType.Double:
                return new FloatingDomain();
            case PhysicalType.ByteArray:
            case PhysicalType.FixedLenByteArray:
                return ForBytes(logical);
            default:
                return null;
        }
    }

    /// <summary>The key of a value from the statistics of a column chunk, or null when it cannot be one (it does not fit what the domain holds).</summary>
    public abstract object? Normalize(object statistic);

    /// <summary>The key of the value of a filter.</summary>
    /// <param name="ordered">Whether keys of this kind of value are ordered as the values are, so that a comparison for order is decided by them.</param>
    /// <exception cref="ArgumentException">The value is of a type that the column cannot be compared with.</exception>
    public abstract object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered);

    public abstract int Compare(object left, object right);

    /// <summary>The plain encoding of a key in a column, to look it up in the bloom filter of the column.</summary>
    public virtual PlainValue GetPlainValue(object key, PrimitiveNode column) => PlainValue.Unsupported;

    /// <summary>Whether a key is a NaN.</summary>
    public virtual bool IsNotANumber(object key) => false;

    protected static ArgumentException Mismatch(object value, IReadOnlyList<string> path, string holds) =>
        new($"A {value.GetType().Name} cannot be compared with the column '{string.Join('.', path)}', which holds {holds}.");

    private static ValueDomain ForInteger(LogicalType? logical) => logical?.Kind switch
    {
        LogicalTypeKind.Decimal => new DecimalDomain(logical.Scale),
        LogicalTypeKind.Date => new TicksDomain(TicksKind.Date, TimeUnit.Millis),
        LogicalTypeKind.Time => new TicksDomain(TicksKind.Time, logical.Unit),
        LogicalTypeKind.Timestamp => new TicksDomain(TicksKind.Timestamp, logical.Unit),
        LogicalTypeKind.Integer => new IntegerDomain(isUnsigned: !logical.IsSigned),
        _ => new IntegerDomain(isUnsigned: false),
    };

    private static ValueDomain ForBytes(LogicalType? logical) => logical?.Kind switch
    {
        LogicalTypeKind.Decimal => new DecimalDomain(logical.Scale),
        LogicalTypeKind.Float16 => new FloatingDomain(),
        LogicalTypeKind.Uuid => new BytesDomain(BytesKind.Uuid),
        LogicalTypeKind.String or LogicalTypeKind.Enum or LogicalTypeKind.Json => new BytesDomain(BytesKind.Text),
        _ => new BytesDomain(BytesKind.Binary),
    };
}
