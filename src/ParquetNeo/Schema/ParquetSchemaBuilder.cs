using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

/// <summary>
/// Describes a <see cref="ParquetSchema"/> with a chain of calls, a field at a time, in place of the nodes of the tree one by one. Every field is
/// required unless <c>optional: true</c> is given, which makes it able to hold nulls; the names of the fields of a struct, a list or a map follow the
/// conventions (<c>element</c>, <c>key</c>, <c>value</c>) so that they need not be given.
/// </summary>
/// <remarks>
/// The columns are the ones <c>ParquetSerializer</c> writes for the same .NET types. A builder can <see cref="Build"/> any number of
/// schemas; each has nodes of its own.
/// </remarks>
/// <example>
/// <code>
/// using static ParquetNeo.Schema.SchemaField;
///
/// var schema = new ParquetSchemaBuilder()
///     .Int64("Id")
///     .String("Name", optional: true)
///     .Decimal("Amount", precision: 18, scale: 4)
///     .Timestamp("CreatedAt")
///     .Struct("Address", optional: true, address => address
///         .String("City")
///         .Int32("Zip", optional: true))
///     .List("Tags", String())
///     .Map("Scores", String(), Int32(optional: true), optional: true)
///     .Build();
///
/// using var writer = ParquetFileWriter.Create(stream, schema);
/// </code>
/// </example>
public sealed class ParquetSchemaBuilder
{
    private readonly List<Func<SchemaNode>> _fields = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>The number of fields added so far.</summary>
    public int Count => _fields.Count;

    /// <summary>A true or false; see <see cref="SchemaField.Boolean"/>.</summary>
    public ParquetSchemaBuilder Boolean(string name, bool optional = false) => Field(name, SchemaField.Boolean(optional));

    /// <summary>A signed 8-bit integer; see <see cref="SchemaField.Int8"/>.</summary>
    public ParquetSchemaBuilder Int8(string name, bool optional = false) => Field(name, SchemaField.Int8(optional));

    /// <summary>A signed 16-bit integer; see <see cref="SchemaField.Int16"/>.</summary>
    public ParquetSchemaBuilder Int16(string name, bool optional = false) => Field(name, SchemaField.Int16(optional));

    /// <summary>A signed 32-bit integer; see <see cref="SchemaField.Int32"/>.</summary>
    public ParquetSchemaBuilder Int32(string name, bool optional = false) => Field(name, SchemaField.Int32(optional));

    /// <summary>A signed 64-bit integer; see <see cref="SchemaField.Int64"/>.</summary>
    public ParquetSchemaBuilder Int64(string name, bool optional = false) => Field(name, SchemaField.Int64(optional));

    /// <summary>An unsigned 8-bit integer; see <see cref="SchemaField.UInt8"/>.</summary>
    public ParquetSchemaBuilder UInt8(string name, bool optional = false) => Field(name, SchemaField.UInt8(optional));

    /// <summary>An unsigned 16-bit integer; see <see cref="SchemaField.UInt16"/>.</summary>
    public ParquetSchemaBuilder UInt16(string name, bool optional = false) => Field(name, SchemaField.UInt16(optional));

    /// <summary>An unsigned 32-bit integer; see <see cref="SchemaField.UInt32"/>.</summary>
    public ParquetSchemaBuilder UInt32(string name, bool optional = false) => Field(name, SchemaField.UInt32(optional));

    /// <summary>An unsigned 64-bit integer; see <see cref="SchemaField.UInt64"/>.</summary>
    public ParquetSchemaBuilder UInt64(string name, bool optional = false) => Field(name, SchemaField.UInt64(optional));

    /// <summary>A 32-bit floating-point number; see <see cref="SchemaField.Float"/>.</summary>
    public ParquetSchemaBuilder Float(string name, bool optional = false) => Field(name, SchemaField.Float(optional));

    /// <summary>A 64-bit floating-point number; see <see cref="SchemaField.Double"/>.</summary>
    public ParquetSchemaBuilder Double(string name, bool optional = false) => Field(name, SchemaField.Double(optional));

    /// <summary>A 16-bit floating-point number; see <see cref="SchemaField.Half"/>.</summary>
    public ParquetSchemaBuilder Half(string name, bool optional = false) => Field(name, SchemaField.Half(optional));

    /// <summary>Text in UTF-8; see <see cref="SchemaField.String"/>.</summary>
    public ParquetSchemaBuilder String(string name, bool optional = false) => Field(name, SchemaField.String(optional));

    /// <summary>Text that is one of a set of names; see <see cref="SchemaField.Enum"/>.</summary>
    public ParquetSchemaBuilder Enum(string name, bool optional = false) => Field(name, SchemaField.Enum(optional));

    /// <summary>A JSON document as text; see <see cref="SchemaField.Json"/>.</summary>
    public ParquetSchemaBuilder Json(string name, bool optional = false) => Field(name, SchemaField.Json(optional));

    /// <summary>Bytes of any length; see <see cref="SchemaField.Binary"/>.</summary>
    public ParquetSchemaBuilder Binary(string name, bool optional = false) => Field(name, SchemaField.Binary(optional));

    /// <summary>Bytes that all have the same length; see <see cref="SchemaField.FixedBinary"/>.</summary>
    public ParquetSchemaBuilder FixedBinary(string name, int length, bool optional = false) => Field(name, SchemaField.FixedBinary(length, optional));

    /// <summary>A <c>Guid</c>; see <see cref="SchemaField.Guid"/>.</summary>
    public ParquetSchemaBuilder Guid(string name, bool optional = false) => Field(name, SchemaField.Guid(optional));

    /// <summary>A day; see <see cref="SchemaField.Date"/>.</summary>
    public ParquetSchemaBuilder Date(string name, bool optional = false) => Field(name, SchemaField.Date(optional));

    /// <summary>A time of day; see <see cref="SchemaField.Time"/>.</summary>
    public ParquetSchemaBuilder Time(string name, TimeUnit unit = TimeUnit.Micros, bool utc = false, bool optional = false) => Field(name, SchemaField.Time(unit, utc, optional));

    /// <summary>A moment; see <see cref="SchemaField.Timestamp"/>.</summary>
    public ParquetSchemaBuilder Timestamp(string name, TimeUnit unit = TimeUnit.Micros, bool utc = true, bool optional = false) => Field(name, SchemaField.Timestamp(unit, utc, optional));

    /// <summary>A decimal number; see <see cref="SchemaField.Decimal"/>.</summary>
    public ParquetSchemaBuilder Decimal(string name, int precision, int scale = 0, bool optional = false) => Field(name, SchemaField.Decimal(precision, scale, optional));

    /// <summary>A column of any physical type and annotation; see <see cref="SchemaField.Column"/>.</summary>
    public ParquetSchemaBuilder Column(string name, PhysicalType type, LogicalType? logicalType = null, bool optional = false, int? typeLength = null) =>
        Field(name, SchemaField.Column(type, logicalType, optional, typeLength));

    /// <summary>A struct: <paramref name="fields"/> adds its fields to the builder it is given.</summary>
    public ParquetSchemaBuilder Struct(string name, Action<ParquetSchemaBuilder> fields) => Field(name, SchemaField.Struct(fields));

    /// <summary>A struct that can be null: <paramref name="fields"/> adds its fields to the builder it is given.</summary>
    public ParquetSchemaBuilder Struct(string name, bool optional, Action<ParquetSchemaBuilder> fields) => Field(name, SchemaField.Struct(fields, optional));

    /// <summary>A list of <paramref name="element"/>; see <see cref="SchemaField.List"/>.</summary>
    public ParquetSchemaBuilder List(string name, SchemaField element, bool optional = false) => Field(name, SchemaField.List(element, optional));

    /// <summary>A map from <paramref name="key"/> to <paramref name="value"/>; see <see cref="SchemaField.Map"/>.</summary>
    public ParquetSchemaBuilder Map(string name, SchemaField key, SchemaField value, bool optional = false) => Field(name, SchemaField.Map(key, value, optional));

    /// <summary>A field made with <see cref="SchemaField"/>, which can be kept and used again.</summary>
    /// <exception cref="ArgumentException">The name is empty, or another field of this level has it.</exception>
    /// <exception cref="ArgumentNullException">The name or the field is null.</exception>
    public ParquetSchemaBuilder Field(string name, SchemaField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Claim(name);
        _fields.Add(() => field.CreateNode(name));
        return this;
    }

    /// <summary>A node made by hand, for what the builder does not have (a repeated field, a field id, a legacy layout). A node belongs to one schema, so a builder with one builds one.</summary>
    /// <exception cref="ArgumentException">Another field of this level has its name.</exception>
    /// <exception cref="ArgumentNullException">The node is null.</exception>
    public ParquetSchemaBuilder Add(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Claim(node.Name);
        _fields.Add(() => node);
        return this;
    }

    /// <summary>Makes the schema.</summary>
    /// <param name="name">The name of the root of the schema, which is not part of the path of any column.</param>
    /// <exception cref="InvalidOperationException">There is no field.</exception>
    /// <exception cref="ArgumentException">A column has an annotation that does not suit its type.</exception>
    public ParquetSchema Build(string name = "schema")
    {
        if (_fields.Count == 0)
        {
            throw new InvalidOperationException("A schema needs at least one field.");
        }

        return new ParquetSchema(CreateNodes(), name);
    }

    /// <summary>New nodes for the fields; used for the schema and for the struct that has this builder for its fields.</summary>
    internal SchemaNode[] CreateNodes()
    {
        if (_fields.Count == 0)
        {
            throw new InvalidOperationException("A struct needs at least one field.");
        }

        return [.. _fields.Select(create => create())];
    }

    private void Claim(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            throw new ArgumentException("The name of a field cannot be empty.", nameof(name));
        }

        if (!_names.Add(name))
        {
            throw new ArgumentException($"There is a field named '{name}' already.", nameof(name));
        }
    }
}
