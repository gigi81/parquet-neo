namespace ParquetNeo.Linq;

/// <summary>What a query reads of a dataset, from the footers of its files; see <see cref="ParquetQueryableExtensions.Explain{T}(IQueryable{T})"/>.</summary>
/// <param name="Files">The number of files in the dataset.</param>
/// <param name="FilesSkipped">How many of them are not read at all, because their statistics rule out every row of them.</param>
/// <param name="RowGroups">The number of row groups in the files that are opened.</param>
/// <param name="RowGroupsSkipped">How many of those are not read, for the same reason.</param>
/// <param name="Filter">The condition that was taken from the query to check against the statistics, or null when none could be.</param>
/// <param name="Columns">The columns that are read, or null when all that the row type has are.</param>
/// <param name="AnsweredFromMetadata">Whether the query is answered from the footers alone, with no data read (the number of rows of a dataset, or whether it has any).</param>
public sealed record ParquetQueryPlan(
    int Files,
    int FilesSkipped,
    int RowGroups,
    int RowGroupsSkipped,
    string? Filter,
    IReadOnlyList<string>? Columns,
    bool AnsweredFromMetadata);
