using ParquetNeo.ColumnTypes;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests;

/// <summary>An amount in cents: a type of the user, which a <see cref="MoneyColumnType"/> teaches the library.</summary>
public readonly record struct Money(long Cents);

/// <summary>Money as a count of cents in an INT64 column, written the way a user of the library would.</summary>
public sealed class MoneyColumnType : ColumnType<Money>
{
    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int64, repetition);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int64 && column.LogicalType is null ? null : "it is not a plain INT64";

    protected override BatchRead ReadCore(ColumnReader reader, Span<Money> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var cents = new long[values.Length];
        var read = reader.ReadBatch(cents, definitionLevels, repetitionLevels);
        for (int i = 0; i < read.Values; i++)
        {
            values[i] = new Money(cents[i]);
        }

        return read;
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<Money> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        var cents = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            cents[i] = values[i].Cents;
        }

        writer.WriteBatch(cents, definitionLevels, repetitionLevels);
    }
}
