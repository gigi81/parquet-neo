using ParquetNeo.Core;

namespace ParquetNeo.DataModel;

/// <summary>
/// How the column of a property is dictionary encoded (every column of a collection or dictionary, but not of the properties of an
/// object inside it, which have their own attributes), instead of <see cref="DictionaryMode.Standard"/>. Use
/// <see cref="DictionaryMode.Adaptive"/> or <see cref="DictionaryMode.Disabled"/> on a property whose values are mostly different,
/// such as an identifier. Modes set in the options win over this one.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetDictionaryAttribute(DictionaryMode mode) : Attribute
{
    public DictionaryMode Mode { get; } = mode;
}
