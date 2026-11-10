namespace ParquetNeo.Serialization.Shredders;

/// <summary>Readers of the elements of collections, of the two kinds a null is told apart in: by reference or by <see cref="Nullable{T}"/>.</summary>
internal static class Getters
{
    public static ItemReader<TValue, TValue> DirectItem<TValue>() =>
        typeof(TValue).IsValueType
            ? (TValue item, out TValue value) =>
            {
                value = item;
                return true;
            }
            : (TValue item, out TValue value) =>
            {
                value = item;
                return value is not null;
            };

    public static ItemReader<TValue?, TValue> NullableItem<TValue>()
        where TValue : struct =>
        (TValue? item, out TValue value) =>
        {
            value = item.GetValueOrDefault();
            return item.HasValue;
        };
}
