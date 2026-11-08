namespace ParquetNeo.ColumnTypes;

/// <summary>The kinds of <see cref="ColumnSettings"/> a column type can take.</summary>
[Flags]
public enum ColumnSettingKinds
{
    None = 0,

    /// <summary>A precision and a scale.</summary>
    Decimal = 1,

    /// <summary>A unit and whether the values are in UTC, for a time of day.</summary>
    Time = 2,

    /// <summary>A unit and whether the values are in UTC, for a moment.</summary>
    Timestamp = 4,
}
