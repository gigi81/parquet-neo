namespace ParquetNeo.ColumnTypes;

/// <summary>
/// Does something that needs the .NET type of a <see cref="ColumnType"/> as a type argument, such as making a buffer of that type,
/// without reflection: <see cref="ColumnType.Accept{TResult}"/> calls it with the type argument the column type knows. That works with
/// Native AOT, where a generic type made at run time from a <see cref="Type"/> may not exist.
/// </summary>
internal interface IColumnTypeVisitor<out TResult>
{
    TResult Visit<TValue>(ColumnType<TValue> type);
}
