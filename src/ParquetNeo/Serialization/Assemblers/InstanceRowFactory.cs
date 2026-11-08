using System.Linq.Expressions;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A row type with a constructor that takes nothing: an instance is created and its properties set.</summary>
internal sealed class InstanceRowFactory<TRow>(Func<TRow> create) : IRowFactory<TRow>
{
    public bool UsesSlots => false;

    /// <summary>The code that creates an instance, for the compiled reading of a row.</summary>
    public Expression Create { get; } = typeof(TRow).IsValueType ? Expression.Default(typeof(TRow)) : Expression.New(typeof(TRow));

    public void Begin(RowContext<TRow> context) => context.Instance = create();

    public TRow End(RowContext<TRow> context) => context.Instance;
}
