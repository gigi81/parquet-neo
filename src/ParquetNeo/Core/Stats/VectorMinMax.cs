using System.Numerics;

namespace ParquetNeo.Core.Stats;

/// <summary>Finds the smallest and largest value of a span, a vector at a time where the hardware allows.</summary>
internal static class VectorMinMax
{
    /// <summary>
    /// Returns false, leaving the outputs unset, when a floating-point span contains a NaN: NaNs have no place in a
    /// minimum or maximum, and vector min/max instructions do not treat them consistently.
    /// </summary>
    /// <param name="values">Must not be empty.</param>
    public static bool TryGetMinMax<T>(ReadOnlySpan<T> values, out T min, out T max)
        where T : unmanaged, INumber<T>
    {
        bool floating = typeof(T) == typeof(float) || typeof(T) == typeof(double);
        int next;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<T>.Count)
        {
            var low = new Vector<T>(values);
            var high = low;
            if (floating && !Vector.EqualsAll(low, low))
            {
                min = max = default;
                return false;
            }

            next = Vector<T>.Count;
            for (; next <= values.Length - Vector<T>.Count; next += Vector<T>.Count)
            {
                var chunk = new Vector<T>(values.Slice(next));
                if (floating && !Vector.EqualsAll(chunk, chunk))
                {
                    min = max = default;
                    return false;
                }

                low = Vector.Min(low, chunk);
                high = Vector.Max(high, chunk);
            }

            min = low[0];
            max = high[0];
            for (int lane = 1; lane < Vector<T>.Count; lane++)
            {
                if (low[lane] < min)
                {
                    min = low[lane];
                }

                if (high[lane] > max)
                {
                    max = high[lane];
                }
            }
        }
        else
        {
            min = max = values[0];
            if (floating && T.IsNaN(min))
            {
                return false;
            }

            next = 1;
        }

        for (; next < values.Length; next++)
        {
            T value = values[next];
            if (floating && T.IsNaN(value))
            {
                return false;
            }

            if (value < min)
            {
                min = value;
            }
            else if (value > max)
            {
                max = value;
            }
        }

        return true;
    }
}
