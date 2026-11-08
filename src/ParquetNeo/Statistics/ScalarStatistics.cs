using System.Numerics;
using System.Runtime.InteropServices;

namespace ParquetNeo.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = Metadata.Statistics;

/// <summary>
/// Statistics of INT32, INT64, FLOAT or DOUBLE values.
/// </summary>
/// <typeparam name="TPhysical">The type of the values as they are written.</typeparam>
/// <typeparam name="TOrdered">
/// The same-sized type whose ordering applies: the physical type itself, or its unsigned counterpart for UINT_* columns
/// (which are stored as the signed type but compare as unsigned).
/// </typeparam>
/// <remarks>
/// Floating-point columns follow the TYPE_ORDER rules of the format: NaNs are counted and left out of the minimum and
/// maximum (which are absent when every value is NaN), a minimum of zero is written as -0.0 and a maximum of zero as +0.0.
/// </remarks>
internal sealed class ScalarStatistics<TPhysical, TOrdered> : StatisticsCollector, IStatisticsSink<TPhysical>
    where TPhysical : unmanaged
    where TOrdered : unmanaged, INumber<TOrdered>
{
    private static readonly bool IsFloating = typeof(TOrdered) == typeof(float) || typeof(TOrdered) == typeof(double);

    private TOrdered _min;
    private TOrdered _max;
    private bool _hasValue;
    private long _nanCount;

    public void Add(ReadOnlySpan<TPhysical> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        var ordered = MemoryMarshal.Cast<TPhysical, TOrdered>(values);
        if (VectorMinMax.TryGetMinMax(ordered, out TOrdered min, out TOrdered max))
        {
            Merge(min, max);
            return;
        }

        // Some value is a NaN: count them and take the minimum and maximum of the rest.
        foreach (TOrdered value in ordered)
        {
            if (TOrdered.IsNaN(value))
            {
                _nanCount++;
            }
            else
            {
                Merge(value, value);
            }
        }
    }

    public override Statistics Build(long nullCount)
    {
        var statistics = new Statistics { NullCount = nullCount };
        if (IsFloating)
        {
            statistics.NanCount = _nanCount;
        }

        if (_hasValue)
        {
            TOrdered min = _min;
            TOrdered max = _max;
            if (IsFloating)
            {
                // Zeros of either sign compare equal, so which one was seen is arbitrary; the format fixes the sign.
                if (min == TOrdered.Zero)
                {
                    min = TOrdered.CreateTruncating(double.NegativeZero);
                }

                if (max == TOrdered.Zero)
                {
                    max = TOrdered.CreateTruncating(0.0);
                }
            }

            statistics.MinValue = ToBytes(min);
            statistics.MaxValue = ToBytes(max);
            statistics.IsMinValueExact = true;
            statistics.IsMaxValueExact = true;
        }

        return statistics;
    }

    private void Merge(TOrdered min, TOrdered max)
    {
        if (!_hasValue)
        {
            _min = min;
            _max = max;
            _hasValue = true;
            return;
        }

        if (min < _min)
        {
            _min = min;
        }

        if (max > _max)
        {
            _max = max;
        }
    }

    private static byte[] ToBytes(TOrdered value)
    {
        var bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<TOrdered>()];
        MemoryMarshal.Write(bytes, in value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }
}
