using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// Everything about storing one .NET type in a Parquet column: the column it gets by default, which columns of a file it can be
/// read from or written to, and how a batch of values is converted to and from the physical type and passed to the column
/// reader or writer. The built-in types are found with <see cref="ColumnTypeRegistry"/>; an own type derives from this class.
/// </summary>
/// <remarks>
/// A type works on batches: the cost of reaching it (a virtual call) is paid once per batch, and the per-value work is generic code
/// that the JIT specializes. The type adapts to the column it is used with, so one instance serves any number of columns; the
/// settings (<see cref="Configure"/>) only decide the column it creates.
/// </remarks>
/// <typeparam name="TValue">The .NET type.</typeparam>
public abstract class ColumnType<TValue> : ColumnType
{
    /// <summary>Creates a column type; call it from the constructor of a type that derives from this class.</summary>
    protected ColumnType()
    {
    }

    /// <inheritdoc/>
    public override string Name => typeof(TValue).Name;

    /// <inheritdoc/>
    public override ColumnSettingKinds SupportedSettings => ColumnSettingKinds.None;

    /// <inheritdoc/>
    public sealed override Type ValueType => typeof(TValue);

    /// <summary>The type with the settings that suit it applied, or this type when there are none.</summary>
    public virtual ColumnType<TValue> Configure(ColumnSettings settings) => this;

    /// <inheritdoc/>
    public sealed override ColumnType WithSettings(ColumnSettings settings) => Configure(settings);

    /// <summary>Reads a batch of the column as values of this type; see <c>ColumnReader.ReadBatch</c> for what the arguments mean.</summary>
    /// <exception cref="ArgumentException">The column cannot be read as this type.</exception>
    /// <exception cref="Exceptions.ValueConversionException">A stored value cannot be represented by the type.</exception>
    public BatchRead Read(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Require(reader.Column);
        return ReadCore(reader, values, definitionLevels, repetitionLevels);
    }

    /// <summary>Writes a batch of values of this type to the column; see <c>ColumnWriter.WriteBatch</c> for what the arguments mean.</summary>
    /// <exception cref="ArgumentException">The column cannot be written as this type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value cannot be stored in the column.</exception>
    public void Write(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Require(writer.Column);
        WriteCore(writer, values, definitionLevels, repetitionLevels);
    }

    /// <summary>Does <see cref="Read"/>, for a column that has been checked.</summary>
    protected abstract BatchRead ReadCore(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels);

    /// <summary>Does <see cref="Write"/>, for a column that has been checked.</summary>
    protected abstract void WriteCore(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels);
}
