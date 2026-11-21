using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

/// <summary>
/// The definition of a field without its name: a column of some type, or a struct, list or map. It is what <see cref="ParquetSchemaBuilder"/>
/// takes where a field has no name of its own (the elements of a list, the keys and values of a map), and it can be kept and used for several
/// names. Every field is required unless <c>optional</c> is asked for, which makes it able to hold nulls.
/// </summary>
/// <remarks>
/// The columns are those <c>ParquetSerializer</c> writes for the same .NET types: <see cref="Int8"/> is an <c>sbyte</c>,
/// <see cref="Timestamp"/> a <c>DateTime</c>, <see cref="Decimal"/> a <c>decimal</c> and so on.
/// </remarks>
/// <example>
/// <code>
/// using static ParquetNeo.Schema.SchemaField;
///
/// var schema = new ParquetSchemaBuilder()
///     .Int64("Id")
///     .Map("Scores", String(), Int32(optional: true))
///     .Build();
/// </code>
/// </example>
public sealed class SchemaField
{
    private readonly Func<string, FieldRepetitionType, SchemaNode> _create;

    private SchemaField(bool optional, Func<string, FieldRepetitionType, SchemaNode> create)
    {
        IsOptional = optional;
        _create = create;
    }

    /// <summary>Whether the field can be null.</summary>
    public bool IsOptional { get; }

    /// <summary>A true or false.</summary>
    public static SchemaField Boolean(bool optional = false) => Column(PhysicalType.Boolean, null, optional);

    /// <summary>A signed 8-bit integer (<c>sbyte</c>), stored as INT32.</summary>
    public static SchemaField Int8(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Integer(8, isSigned: true), optional);

    /// <summary>A signed 16-bit integer (<c>short</c>), stored as INT32.</summary>
    public static SchemaField Int16(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Integer(16, isSigned: true), optional);

    /// <summary>A signed 32-bit integer (<c>int</c>).</summary>
    public static SchemaField Int32(bool optional = false) => Column(PhysicalType.Int32, null, optional);

    /// <summary>A signed 64-bit integer (<c>long</c>).</summary>
    public static SchemaField Int64(bool optional = false) => Column(PhysicalType.Int64, null, optional);

    /// <summary>An unsigned 8-bit integer (<c>byte</c>), stored as INT32.</summary>
    public static SchemaField UInt8(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Integer(8, isSigned: false), optional);

    /// <summary>An unsigned 16-bit integer (<c>ushort</c>), stored as INT32.</summary>
    public static SchemaField UInt16(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Integer(16, isSigned: false), optional);

    /// <summary>An unsigned 32-bit integer (<c>uint</c>), stored as INT32.</summary>
    public static SchemaField UInt32(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Integer(32, isSigned: false), optional);

    /// <summary>An unsigned 64-bit integer (<c>ulong</c>), stored as INT64.</summary>
    public static SchemaField UInt64(bool optional = false) => Column(PhysicalType.Int64, LogicalType.Integer(64, isSigned: false), optional);

    /// <summary>A 32-bit floating-point number (<c>float</c>).</summary>
    public static SchemaField Float(bool optional = false) => Column(PhysicalType.Float, null, optional);

    /// <summary>A 64-bit floating-point number (<c>double</c>).</summary>
    public static SchemaField Double(bool optional = false) => Column(PhysicalType.Double, null, optional);

    /// <summary>A 16-bit floating-point number (<c>Half</c>), stored as a 2-byte FIXED_LEN_BYTE_ARRAY annotated FLOAT16.</summary>
    public static SchemaField Half(bool optional = false) => Column(PhysicalType.FixedLenByteArray, LogicalType.Float16, optional, typeLength: 2);

    /// <summary>Text in UTF-8 (<c>string</c>).</summary>
    public static SchemaField String(bool optional = false) => Column(PhysicalType.ByteArray, LogicalType.String, optional);

    /// <summary>Text that is one of a set of names, annotated ENUM (what an enum property is written as).</summary>
    public static SchemaField Enum(bool optional = false) => Column(PhysicalType.ByteArray, LogicalType.Enum, optional);

    /// <summary>A JSON document as text, annotated JSON.</summary>
    public static SchemaField Json(bool optional = false) => Column(PhysicalType.ByteArray, LogicalType.Json, optional);

    /// <summary>Bytes of any length (<c>byte[]</c>), with no annotation.</summary>
    public static SchemaField Binary(bool optional = false) => Column(PhysicalType.ByteArray, null, optional);

    /// <summary>Bytes that all have the same <paramref name="length"/>, with no annotation.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.</exception>
    public static SchemaField FixedBinary(int length, bool optional = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return Column(PhysicalType.FixedLenByteArray, null, optional, length);
    }

    /// <summary>A <c>Guid</c>, stored as a 16-byte FIXED_LEN_BYTE_ARRAY annotated UUID.</summary>
    public static SchemaField Guid(bool optional = false) => Column(PhysicalType.FixedLenByteArray, LogicalType.Uuid, optional, typeLength: 16);

    /// <summary>A day (<c>DateOnly</c>), stored as INT32 as the days since 1970-01-01.</summary>
    public static SchemaField Date(bool optional = false) => Column(PhysicalType.Int32, LogicalType.Date, optional);

    /// <summary>A time of day (<c>TimeOnly</c>): milliseconds are stored as INT32, microseconds and nanoseconds as INT64.</summary>
    /// <param name="unit">The resolution.</param>
    /// <param name="utc">Whether the time is in UTC, as opposed to a reading of the clock on the wall.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is not a unit.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Time(TimeUnit unit = TimeUnit.Micros, bool utc = false, bool optional = false)
    {
        RequireUnit(unit);
        return Column(unit == TimeUnit.Millis ? PhysicalType.Int32 : PhysicalType.Int64, LogicalType.Time(utc, unit), optional);
    }

    /// <summary>A moment (<c>DateTime</c> or <c>DateTimeOffset</c>), stored as INT64 as the count of the unit since 1970-01-01.</summary>
    /// <param name="unit">The resolution.</param>
    /// <param name="utc">Whether the moment is in UTC, as opposed to a reading of the clock on the wall.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is not a unit.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Timestamp(TimeUnit unit = TimeUnit.Micros, bool utc = true, bool optional = false)
    {
        RequireUnit(unit);
        return Column(PhysicalType.Int64, LogicalType.Timestamp(utc, unit), optional);
    }

    /// <summary>
    /// A decimal number (<c>decimal</c>) of <paramref name="precision"/> digits, <paramref name="scale"/> of them after the decimal point. It is stored as
    /// INT32 up to a precision of 9, INT64 up to 18, and otherwise as a FIXED_LEN_BYTE_ARRAY as short as holds the precision.
    /// </summary>
    /// <param name="precision">From 1 to 38.</param>
    /// <param name="scale">From 0 to the precision.</param>
    /// <exception cref="ArgumentOutOfRangeException">The precision or the scale is out of its range.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Decimal(int precision, int scale = 0, bool optional = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 38);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, precision);
        var logical = LogicalType.Decimal(scale, precision);
        if (precision <= 9)
        {
            return Column(PhysicalType.Int32, logical, optional);
        }

        if (precision <= 18)
        {
            return Column(PhysicalType.Int64, logical, optional);
        }

        int length = 1;
        while (LogicalTypeRules.MaxDecimalPrecision(length) < precision)
        {
            length++;
        }

        return Column(PhysicalType.FixedLenByteArray, logical, optional, length);
    }

    /// <summary>A column of any physical type and annotation, for what the others do not have.</summary>
    /// <param name="type">The physical type.</param>
    /// <param name="logicalType">The annotation, or null for none.</param>
    /// <param name="typeLength">The length of the values of a FIXED_LEN_BYTE_ARRAY.</param>
    /// <exception cref="ArgumentException">At <see cref="ParquetSchemaBuilder.Build"/>: the annotation or the length does not suit the type.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Column(PhysicalType type, LogicalType? logicalType = null, bool optional = false, int? typeLength = null) =>
        new(optional, (name, repetition) => new PrimitiveNode(name, type, repetition, logicalType, typeLength));

    /// <summary>A group of fields, written as the nested columns of a struct or an object.</summary>
    /// <param name="fields">Adds the fields of the struct to the builder it is given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is null.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Struct(Action<ParquetSchemaBuilder> fields, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var builder = new ParquetSchemaBuilder();
        fields(builder);
        return new(optional, (name, repetition) => new GroupNode(name, repetition, builder.CreateNodes()));
    }

    /// <summary>A list of <paramref name="element"/>, in the standard layout (a LIST group of a repeated group of the element).</summary>
    /// <param name="element">What the list holds. Make it optional for a list that can hold nulls.</param>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField List(SchemaField element, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new(optional, (name, repetition) => Nested.NestedSchema.List(name, repetition, element.CreateNode("element")));
    }

    /// <summary>A map from <paramref name="key"/> to <paramref name="value"/>, in the standard layout (a MAP group of a repeated group of both).</summary>
    /// <param name="key">What the keys are; it cannot be optional, since a key is never null.</param>
    /// <param name="value">What the values are; make it optional for values that can be null.</param>
    /// <exception cref="ArgumentNullException">The key or the value is null.</exception>
    /// <exception cref="ArgumentException">The key is optional.</exception>
    /// <param name="optional">Whether the value can be null: the field is optional.</param>
    public static SchemaField Map(SchemaField key, SchemaField value, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.IsOptional)
        {
            throw new ArgumentException("The key of a map cannot be optional.", nameof(key));
        }

        return new(optional, (name, repetition) => Nested.NestedSchema.Map(name, repetition, key.CreateNode("key"), value.CreateNode("value")));
    }

    /// <summary>Makes the node of this field with the name it has in its parent; a new node every time, since a node belongs to one schema.</summary>
    internal SchemaNode CreateNode(string name) => _create(name, IsOptional ? FieldRepetitionType.Optional : FieldRepetitionType.Required);

    private static void RequireUnit(TimeUnit unit)
    {
        if (!System.Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "The unit must be milliseconds, microseconds or nanoseconds.");
        }
    }
}
