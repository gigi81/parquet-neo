using ParquetNeo.ColumnTypes;
using ParquetNeo.Schema;

namespace ParquetNeo.Data;

/// <summary>Chooses the .NET type a column of a file is read as.</summary>
internal static class FileColumnTypes
{
    /// <summary>
    /// The types to try, the most specific first: a column fits several (a signed 8-bit integer also fits <see cref="int"/>), and
    /// the first that accepts it is the one it is read as.
    /// </summary>
    private static readonly Type[] Candidates =
    [
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(uint), typeof(ulong), typeof(Half), typeof(Guid),
        typeof(DateOnly), typeof(TimeOnly), typeof(DateTime), typeof(decimal), typeof(bool), typeof(int), typeof(long),
        typeof(float), typeof(double), typeof(byte[]), typeof(string),
    ];

    /// <summary>The column type for the column, or null when no .NET type fits it, such as an unannotated FIXED_LEN_BYTE_ARRAY.</summary>
    public static ColumnType? Choose(PrimitiveNode column) =>
        Candidates.Select(ColumnTypeRegistry.TryGet).FirstOrDefault(type => type is not null && type.GetProblem(column) is null);
}
