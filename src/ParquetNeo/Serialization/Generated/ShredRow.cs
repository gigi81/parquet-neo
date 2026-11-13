namespace ParquetNeo.Serialization.Generated;

/// <summary>Gives the values of a row to the columns, which is what the source generator writes for each type.</summary>
public delegate void ShredRow<in TRow>(TRow row, RowSink sink);

/// <summary>Creates a row from the values of the columns, which is what the source generator writes for each type.</summary>
public delegate TRow AssembleRow<out TRow>(RowSource source);
