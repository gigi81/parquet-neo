using ParquetNeo.Metadata;

namespace ParquetNeo.Core.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = ParquetNeo.Metadata.Statistics;

/// <summary>Statistics of BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY values.</summary>
/// <remarks>
/// With unsigned byte-wise order, values longer than the truncation length are stored shortened (see
/// <see cref="BinaryTruncation"/>) and the matching <c>is_*_value_exact</c> flag is false. A maximum that cannot be
/// shortened safely is left out. Memory use stays bounded by the truncation length however long the values are.
/// </remarks>
internal sealed class BinaryStatistics : StatisticsCollector
{
    private readonly BinaryOrder _order;
    private readonly bool _utf8;
    private readonly int _limit;

    private byte[] _min = [];
    private int _minLength;
    private bool _hasMin;
    private bool _minExact;

    private byte[] _max = [];
    private int _maxLength;
    private bool _hasMax;
    private bool _maxExact;
    private bool _maxUnavailable;

    /// <param name="utf8">Whether the values are text, so that shortening must not split a character.</param>
    /// <param name="truncateLength">The longest minimum or maximum to keep; only applies to unsigned byte-wise order.</param>
    public BinaryStatistics(BinaryOrder order, bool utf8, int truncateLength)
    {
        _order = order;
        _utf8 = utf8;
        _limit = order == BinaryOrder.UnsignedBytes ? Math.Max(truncateLength, 1) : int.MaxValue;
    }

    public override void AddBinary(ReadOnlySpan<byte> value)
    {
        if (!_hasMin || Compare(value, _min.AsSpan(0, _minLength)) < 0)
        {
            int length = BinaryTruncation.PrefixLength(value, _limit, _utf8);
            Store(ref _min, value[..length]);
            _minLength = length;
            _minExact = length == value.Length;
            _hasMin = true;
        }

        if (_maxUnavailable || (_hasMax && Compare(value, _max.AsSpan(0, _maxLength)) <= 0))
        {
            return;
        }

        if (value.Length <= _limit)
        {
            Store(ref _max, value);
            _maxLength = value.Length;
            _maxExact = true;
            _hasMax = true;
        }
        else if (BinaryTruncation.TryCreateUpperBound(value, _limit, _utf8, out byte[] bound))
        {
            Store(ref _max, bound);
            _maxLength = bound.Length;
            _maxExact = false;
            _hasMax = true;
        }
        else
        {
            _maxUnavailable = true;
            _hasMax = false;
        }
    }

    public override Statistics Build(long nullCount)
    {
        var statistics = new Statistics { NullCount = nullCount };
        if (_hasMin)
        {
            statistics.MinValue = _min.AsSpan(0, _minLength).ToArray();
            statistics.IsMinValueExact = _minExact;
        }

        if (_hasMax && !_maxUnavailable)
        {
            statistics.MaxValue = _max.AsSpan(0, _maxLength).ToArray();
            statistics.IsMaxValueExact = _maxExact;
        }

        return statistics;
    }

    private int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        _order == BinaryOrder.UnsignedBytes ? left.SequenceCompareTo(right) : CompareSigned(left, right);

    /// <summary>Compares two-complement big-endian numbers of possibly different lengths.</summary>
    internal static int CompareSigned(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte leftSign = SignByte(left);
        byte rightSign = SignByte(right);
        if (leftSign != rightSign)
        {
            return leftSign == 0 ? 1 : -1;
        }

        // Same sign: the shorter number is extended on the left with its sign byte, then the bytes compare unsigned.
        int length = Math.Max(left.Length, right.Length);
        int leftPad = length - left.Length;
        int rightPad = length - right.Length;
        for (int i = 0; i < length; i++)
        {
            byte l = i < leftPad ? leftSign : left[i - leftPad];
            byte r = i < rightPad ? rightSign : right[i - rightPad];
            if (l != r)
            {
                return l < r ? -1 : 1;
            }
        }

        return 0;
    }

    private static byte SignByte(ReadOnlySpan<byte> number) => !number.IsEmpty && (number[0] & 0x80) != 0 ? (byte)0xFF : (byte)0;

    private static void Store(ref byte[] buffer, ReadOnlySpan<byte> value)
    {
        if (buffer.Length < value.Length)
        {
            buffer = new byte[Math.Max(value.Length, Math.Min(buffer.Length * 2, 1024))];
        }

        value.CopyTo(buffer);
    }
}
