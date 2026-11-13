namespace ParquetNeo.DataModel;

/// <summary>
/// Asks the source generator for the mapping of a type, so that the serializer never looks at the type by reflection: it works with
/// trimming and Native AOT, and does not compile code when it is first used. Put it on the type, or, for a type you do not own, on the
/// assembly: <c>[assembly: ParquetSerializable(typeof(TheirType))]</c>. The mapping is registered when the assembly is loaded, so
/// <see cref="Serialization.ParquetSerializer"/> is used as before. A type without it keeps working by reflection, which is not
/// safe with trimming or Native AOT.
/// </summary>
/// <remarks>
/// The same properties are columns as without it, with the same attributes. A type the generator cannot map yet (a property that
/// is an object or a collection, or a type built by a constructor with parameters) gets a warning and is left to reflection.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ParquetSerializableAttribute : Attribute
{
    /// <summary>Marks the type it is put on.</summary>
    public ParquetSerializableAttribute()
    {
    }

    /// <summary>Marks a type that cannot be changed; use it on the assembly.</summary>
    public ParquetSerializableAttribute(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>The type, when the attribute is on the assembly.</summary>
    public Type? Type { get; }
}
