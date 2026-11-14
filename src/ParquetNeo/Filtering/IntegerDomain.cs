using ParquetNeo.Schema;

namespace ParquetNeo.Filtering;

/// <summary>Integers of any width and signedness, as <see cref="Int128"/>s: an unsigned column is stored in a signed integer of its width, which the key undoes.</summary>
internal sealed class IntegerDomain(bool isUnsigned) : ValueDomain
{
    public override object? Normalize(object statistic) => statistic switch
    {
        int value => isUnsigned ? (Int128)(uint)value : value,
        long value => isUnsigned ? (Int128)(ulong)value : value,
        _ => null,
    };

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        return value switch
        {
            sbyte number => (Int128)number,
            byte number => (Int128)number,
            short number => (Int128)number,
            ushort number => (Int128)number,
            int number => (Int128)number,
            uint number => (Int128)number,
            long number => (Int128)number,
            ulong number => (Int128)number,
            _ => throw Mismatch(value, path, "integers"),
        };
    }

    public override int Compare(object left, object right) => ((Int128)left).CompareTo((Int128)right);

    public override PlainValue GetPlainValue(object key, PrimitiveNode column)
    {
        var value = (Int128)key;
        if (column.Type == PhysicalType.Int32)
        {
            if (isUnsigned)
            {
                return Within(value, 0, uint.MaxValue) ? PlainValue.Of(unchecked((int)(uint)value)) : PlainValue.NotStorable;
            }

            return Within(value, int.MinValue, int.MaxValue) ? PlainValue.Of((int)value) : PlainValue.NotStorable;
        }

        if (isUnsigned)
        {
            return Within(value, 0, ulong.MaxValue) ? PlainValue.Of(unchecked((long)(ulong)value)) : PlainValue.NotStorable;
        }

        return Within(value, long.MinValue, long.MaxValue) ? PlainValue.Of((long)value) : PlainValue.NotStorable;
    }

    private static bool Within(Int128 value, Int128 low, Int128 high) => value >= low && value <= high;
}
