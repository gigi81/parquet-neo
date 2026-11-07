namespace ParquetNeo.Serialization;

/// <summary>
/// The precision and scale of a <see cref="decimal"/> property's column. Without it a decimal is written with a precision of 38
/// and a scale of 18. A value with more decimals than the scale, or more digits than the precision, cannot be written.
/// The column is INT32 up to a precision of 9, INT64 up to 18, and a fixed-length byte array above that.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetDecimalAttribute : Attribute
{
    /// <param name="precision">The number of digits, from 1 to 38.</param>
    /// <param name="scale">The number of digits after the decimal point, from 0 to the precision and at most 28, the most a <see cref="decimal"/> has.</param>
    public ParquetDecimalAttribute(int precision, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 38);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, Math.Min(precision, 28));
        Precision = precision;
        Scale = scale;
    }

    public int Precision { get; }

    public int Scale { get; }
}
