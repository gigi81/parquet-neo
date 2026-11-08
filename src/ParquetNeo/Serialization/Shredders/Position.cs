namespace ParquetNeo.Serialization.Shredders;

/// <summary>Where a part of the row is in the schema: how many optional and repeated nodes lie above it.</summary>
internal readonly record struct Position(int Definition, int Repetition);
