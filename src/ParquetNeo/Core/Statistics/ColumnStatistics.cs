namespace ParquetNeo.Core.Statistics;

/// <summary>
/// The statistics of one column chunk, decoded to .NET values. Obtain it from
/// <see cref="RowGroupReader.GetStatistics(int)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Min"/> and <see cref="Max"/> are physical values: <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="float"/>, <see cref="double"/> or <c>byte[]</c> (BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY), the same types the column
/// is read as. They are ordered the way the column's type defines, which is not always the order of the .NET type:
/// unsigned integer columns (UINT_8 to UINT_64) are stored in <see cref="int"/> and <see cref="long"/> but compare
/// unsigned; strings and binary compare as unsigned bytes; decimals as big-endian two's complement numbers.
/// </para>
/// <para>
/// They are bounds and may not be values in the column: when <see cref="IsMinExact"/> or <see cref="IsMaxExact"/> is not
/// <c>true</c>, the bound may be shortened or rounded. A value is <c>null</c> when it was not recorded or cannot be trusted
/// (for example the column order is unknown, or a floating-point bound is NaN).
/// </para>
/// </remarks>
/// <param name="Min">A lower bound of the non-null, non-NaN values.</param>
/// <param name="Max">An upper bound of the non-null, non-NaN values.</param>
/// <param name="NullCount">How many rows are null.</param>
/// <param name="NanCount">How many values are NaN (floating-point columns only). When absent, NaNs may be present.</param>
/// <param name="IsMinExact">Whether <paramref name="Min"/> is the actual minimum; <c>null</c> if the writer did not say.</param>
/// <param name="IsMaxExact">Whether <paramref name="Max"/> is the actual maximum; <c>null</c> if the writer did not say.</param>
public sealed record ColumnStatistics(
    object? Min,
    object? Max,
    long? NullCount,
    long? NanCount,
    bool? IsMinExact,
    bool? IsMaxExact);
