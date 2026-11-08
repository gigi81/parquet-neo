namespace ParquetNeo.Serialization.Assemblers;

/// <summary>How the row type is matched to one file: the column of the file for each column of the row type, and the facts about each node.</summary>
/// <param name="FileColumns">For each column of the row type (its slot), its index among the columns of the file, or -1 when the file has none.</param>
/// <param name="Nodes">For each object, collection and dictionary of the row type (its node number), what it needs to know about the file.</param>
internal sealed record ReadPlan(int[] FileColumns, NodeBinding[] Nodes);
