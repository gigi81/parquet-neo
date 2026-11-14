using System.Numerics;

namespace ParquetNeo.Filtering;

/// <summary>Decimals, as <see cref="decimal"/>s: the statistics hold the unscaled number, in an integer or as big-endian bytes.</summary>
internal sealed class DecimalDomain(int scale) : ValueDomain
{
    public override object? Normalize(object statistic) => statistic switch
    {
        int value => ToDecimal(value),
        long value => ToDecimal(value),
        byte[] { Length: > 0 } bytes => ToDecimal(new BigInteger(bytes, isUnsigned: false, isBigEndian: true)),
        _ => null,
    };

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        return value switch
        {
            decimal number => number,
            sbyte number => (decimal)number,
            byte number => (decimal)number,
            short number => (decimal)number,
            ushort number => (decimal)number,
            int number => (decimal)number,
            uint number => (decimal)number,
            long number => (decimal)number,
            ulong number => (decimal)number,
            _ => throw Mismatch(value, path, "decimals"),
        };
    }

    public override int Compare(object left, object right) => ((decimal)left).CompareTo((decimal)right);

    /// <summary>The number the unscaled integer stands for, or null when a <see cref="decimal"/> cannot hold it.</summary>
    private object? ToDecimal(BigInteger unscaled)
    {
        var magnitude = BigInteger.Abs(unscaled);
        if (scale is < 0 or > 28 || magnitude >> 96 != BigInteger.Zero)
        {
            return null;
        }

        var bits = (UInt128)magnitude;
        return new decimal((int)(uint)bits, (int)(uint)(bits >> 32), (int)(uint)(bits >> 64), unscaled.Sign < 0, (byte)scale);
    }
}
