
namespace ParquetNeo.ColumnTypes;

/// <summary>
/// What can be adjusted about the column a type is written to. A type takes the settings that suit it (see
/// <see cref="ColumnType{TValue}.SupportedSettings"/>) and ignores the others.
/// </summary>
public sealed record ColumnSettings
{
    /// <summary>The number of digits of a decimal.</summary>
    public int? Precision { get; init; }

    /// <summary>The number of digits of a decimal after the decimal point.</summary>
    public int? Scale { get; init; }

    /// <summary>The resolution of a time or timestamp.</summary>
    public TimeUnit? Unit { get; init; }

    /// <summary>Whether a time or timestamp is in UTC, as opposed to a reading of the clock on the wall.</summary>
    public bool? IsAdjustedToUtc { get; init; }
}
