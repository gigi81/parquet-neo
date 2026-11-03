namespace ParquetNeo.Encodings;

/// <summary>
/// The deprecated 12-byte INT96 value, still found in legacy timestamp columns. It is stored as the
/// nanoseconds within the day (first 8 bytes) followed by the Julian day number (last 4 bytes).
/// </summary>
internal readonly struct Int96
{
    private readonly uint _part0;
    private readonly uint _part1;
    private readonly uint _part2;

    public Int96(long nanosOfDay, int julianDay)
    {
        _part0 = (uint)nanosOfDay;
        _part1 = (uint)((ulong)nanosOfDay >> 32);
        _part2 = (uint)julianDay;
    }

    public long NanosOfDay => (long)(((ulong)_part1 << 32) | _part0);

    public int JulianDay => (int)_part2;
}
