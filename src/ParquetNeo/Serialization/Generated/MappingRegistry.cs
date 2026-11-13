namespace ParquetNeo.Serialization.Generated;

/// <summary>
/// Where the source generator registers the mapping of each type marked <see cref="DataModel.ParquetSerializableAttribute"/>, when the
/// assembly is loaded. The mapping is made the first time the serializer needs it. Not for use in code you write yourself.
/// </summary>
public static class MappingRegistry
{
    /// <summary>Makes the serializer write rows of <typeparamref name="TRow"/> with the mapping the function makes, instead of looking at the type by reflection.</summary>
    public static void RegisterWrite<TRow>(Func<WriteMapping<TRow>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        RowModel<TRow>.Register(() => create().Model);
    }

    /// <summary>Makes the serializer read rows of <typeparamref name="TRow"/> with the mapping the function makes, instead of looking at the type by reflection.</summary>
    public static void RegisterRead<TRow>(Func<ReadMapping<TRow>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        ReadModel<TRow>.Register(() => create().Model);
    }
}
