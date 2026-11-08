using System.Data;

namespace ParquetNeo.Data;

/// <summary>Finds how to take a value of a .NET type from a row without boxing it, through the typed getter of <see cref="IDataRecord"/> that returns it.</summary>
internal static class ColumnGetters
{
    private static readonly Dictionary<Type, Delegate> Typed = new()
    {
        [typeof(bool)] = (Func<IDataRecord, int, bool>)((r, i) => r.GetBoolean(i)),
        [typeof(byte)] = (Func<IDataRecord, int, byte>)((r, i) => r.GetByte(i)),
        [typeof(short)] = (Func<IDataRecord, int, short>)((r, i) => r.GetInt16(i)),
        [typeof(int)] = (Func<IDataRecord, int, int>)((r, i) => r.GetInt32(i)),
        [typeof(long)] = (Func<IDataRecord, int, long>)((r, i) => r.GetInt64(i)),
        [typeof(float)] = (Func<IDataRecord, int, float>)((r, i) => r.GetFloat(i)),
        [typeof(double)] = (Func<IDataRecord, int, double>)((r, i) => r.GetDouble(i)),
        [typeof(decimal)] = (Func<IDataRecord, int, decimal>)((r, i) => r.GetDecimal(i)),
        [typeof(string)] = (Func<IDataRecord, int, string>)((r, i) => r.GetString(i)),
        [typeof(DateTime)] = (Func<IDataRecord, int, DateTime>)((r, i) => r.GetDateTime(i)),
        [typeof(Guid)] = (Func<IDataRecord, int, Guid>)((r, i) => r.GetGuid(i)),
    };

    /// <summary>The typed getter for the type, or one that unboxes <see cref="IDataRecord.GetValue"/> when the record has none.</summary>
    public static Func<IDataRecord, int, TValue> For<TValue>() =>
        Typed.TryGetValue(typeof(TValue), out var getter) ? (Func<IDataRecord, int, TValue>)getter : (r, i) => (TValue)r.GetValue(i);
}
