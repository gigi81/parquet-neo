using System.Buffers.Binary;
using ParquetNeo.Schema;

namespace ParquetNeo.Filtering;

/// <summary>Floating-point numbers, as <see cref="double"/>s: every one of the three widths fits one exactly.</summary>
internal sealed class FloatingDomain : ValueDomain
{
    public override bool HasNotANumbers => true;

    public override object? Normalize(object statistic) => statistic switch
    {
        float value => (double)value,
        double value => value,
        byte[] { Length: 2 } bytes => (double)BinaryPrimitives.ReadHalfLittleEndian(bytes),
        _ => null,
    };

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        return value switch
        {
            double number => number,
            float number => (double)number,
            Half number => (double)number,
            sbyte number => (double)number,
            byte number => (double)number,
            short number => (double)number,
            ushort number => (double)number,
            int number => (double)number,
            uint number => (double)number,
            long number => (double)number,
            ulong number => (double)number,
            _ => throw Mismatch(value, path, "floating-point numbers"),
        };
    }

    public override int Compare(object left, object right) => ((double)left).CompareTo((double)right);

    public override bool IsNotANumber(object key) => double.IsNaN((double)key);

    public override PlainValue GetPlainValue(object key, PrimitiveNode column)
    {
        double value = (double)key;

        // Zero is stored as +0 or -0, which are different bytes and the same number.
        if (Math.Abs(value) < double.Epsilon || column.Type is not (PhysicalType.Float or PhysicalType.Double))
        {
            return PlainValue.Unsupported;
        }

        if (column.Type == PhysicalType.Double)
        {
            return PlainValue.Of(BitConverter.DoubleToInt64Bits(value));
        }

        float narrow = (float)value;
        return BitConverter.DoubleToInt64Bits((double)narrow) == BitConverter.DoubleToInt64Bits(value) ? PlainValue.Of(BitConverter.SingleToInt32Bits(narrow)) : PlainValue.NotStorable;
    }
}
