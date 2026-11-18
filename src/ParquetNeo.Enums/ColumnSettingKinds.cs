namespace ParquetNeo;

/// <summary>The kinds of <c>ColumnSettings</c> a column type can take.</summary>
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

    /// <summary>A coordinate reference system, and for a geography the algorithm of its edges.</summary>
    Geospatial = 8,
}
