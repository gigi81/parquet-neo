namespace ParquetNeo.Serialization.Assemblers;

/// <summary>
/// What an object, a collection or a dictionary of the row type needs to know about the node of the file it is read from. These
/// depend on the file, whose nodes may be optional or required whatever the row type says, so they are worked out for every file.
/// </summary>
/// <param name="Controller">The slot of a column below it that the file has, whose entries tell where the object or collection starts and ends; -1 when the file has none.</param>
/// <param name="PresentDefinition">The definition level from which the object, collection or dictionary is not null.</param>
/// <param name="ElementsDefinition">The definition level from which a collection or dictionary holds at least one element.</param>
/// <param name="RepetitionLevel">The repetition level of an entry that continues a collection or dictionary instead of starting one.</param>
internal readonly record struct NodeBinding(int Controller, int PresentDefinition, int ElementsDefinition, int RepetitionLevel);
