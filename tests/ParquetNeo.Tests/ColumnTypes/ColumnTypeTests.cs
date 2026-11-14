using ParquetNeo.ColumnTypes;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.ColumnTypes;

public class ColumnTypeTests
{
    private static readonly Type[] BuiltIn =
    [
        typeof(bool), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(Half), typeof(decimal), typeof(string), typeof(byte[]), typeof(Guid), typeof(DateTime),
        typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(DayOfWeek),
    ];

    private static byte[] Write(PrimitiveNode node, Action<ColumnWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), options: null, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            write(group.NextColumn());
        }

        return stream.ToArray();
    }

    private static ColumnReader Open(byte[] bytes) => ReadHelper.Open(bytes).OpenRowGroup(0).OpenColumn(0);

    // ---- the registry ----

    [Test]
    public async Task TheRegistry_HasAColumnTypeForEveryBuiltInType_AndEnums()
    {
        foreach (var type in BuiltIn)
        {
            var columnType = ColumnTypeRegistry.TryGet(type);

            await Assert.That(columnType).IsNotNull().Because(type.Name);
            await Assert.That(columnType!.ValueType).IsEqualTo(type);
            await Assert.That(ColumnTypeRegistry.IsSupported(type)).IsTrue();
        }

        await Assert.That(ColumnTypeRegistry.IsSupported(typeof(object))).IsFalse();
        await Assert.That(ColumnTypeRegistry.TryGet<object>()).IsNull();
        await Assert.That(() => ColumnTypeRegistry.Get<object>()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task TheColumnTypeOfAnEnum_IsMadeOncePerEnum()
    {
        await Assert.That(ColumnTypeRegistry.Get<DayOfWeek>()).IsSameReferenceAs(ColumnTypeRegistry.Get<DayOfWeek>());
        await Assert.That(ColumnTypeRegistry.Get<DayOfWeek>()).IsNotSameReferenceAs(ColumnTypeRegistry.Get<DateTimeKind>());
    }

    [Test]
    public async Task AnEnumRegisteredByName_IsTheOneTheRegistryFindsAfterwards()
    {
        ColumnTypeRegistry.RegisterEnum<StringComparison>();
        var registered = ColumnTypeRegistry.Get<StringComparison>();

        await Assert.That(ColumnTypeRegistry.TryGet(typeof(StringComparison))).IsSameReferenceAs(registered);
        await Assert.That(registered.ValueType).IsEqualTo(typeof(StringComparison));

        // Registering again keeps the first one, so what was handed out stays the one in use.
        ColumnTypeRegistry.RegisterEnum<StringComparison>();
        await Assert.That(ColumnTypeRegistry.Get<StringComparison>()).IsSameReferenceAs(registered);
    }

    [Test]
    public async Task AColumnType_HandsItsValueTypeToAVisitorAsATypeArgument()
    {
        foreach (var type in new ColumnType[] { ColumnTypeRegistry.Get<long>(), ColumnTypeRegistry.Get<string>(), ColumnTypeRegistry.Get<DayOfWeek>(), ColumnTypeRegistry.Get<decimal>() })
        {
            await Assert.That(type.Accept(new TypeNameVisitor())).IsEqualTo(type.ValueType.Name);
        }
    }

    private sealed class TypeNameVisitor : IColumnTypeVisitor<string>
    {
        public string Visit<TValue>(ColumnType<TValue> type) => typeof(TValue).Name;
    }

    [Test]
    public async Task EveryColumnTypeAcceptsTheColumnItCreates_AndNoOtherBuiltInOneAcceptsIt()
    {
        foreach (var type in BuiltIn.Where(t => !t.IsEnum))
        {
            var columnType = ColumnTypeRegistry.TryGet(type)!;
            var node = columnType.CreateNode("c", FieldRepetitionType.Required);

            await Assert.That(columnType.GetProblem(node)).IsNull().Because(type.Name);
        }

        // Types on the same physical type do not take each other's columns.
        var date = ColumnTypeRegistry.Get<DateOnly>().CreateNode("c", FieldRepetitionType.Required);
        await Assert.That(ColumnTypeRegistry.Get<int>().GetProblem(date)).IsNotNull();
        await Assert.That(ColumnTypeRegistry.Get<uint>().GetProblem(date)).IsNotNull();
        await Assert.That(ColumnTypeRegistry.Get<DateOnly>().GetProblem(date)).IsNull();
    }

    [Test]
    public async Task AColumnThatDoesNotFit_IsRefusedWithAWordedReason()
    {
        var text = ColumnTypeRegistry.Get<string>().CreateNode("name", FieldRepetitionType.Required);

        string? problem = ColumnTypeRegistry.Get<int>().GetProblem(text);
        var exception = Assert.Throws<ArgumentException>(() => ColumnTypeRegistry.Get<int>().Require(text));

        await Assert.That(problem).Contains("BYTE_ARRAY").And.Contains("STRING");
        await Assert.That(exception.Message).Contains("'name'").And.Contains("cannot be used as int");
    }

    // ---- settings ----

    [Test]
    public async Task OnlyTheTypesThatTakeSettings_SayTheyDo()
    {
        await Assert.That(ColumnTypeRegistry.Get<decimal>().SupportedSettings).IsEqualTo(ColumnSettingKinds.Decimal);
        await Assert.That(ColumnTypeRegistry.Get<TimeOnly>().SupportedSettings).IsEqualTo(ColumnSettingKinds.Time);
        await Assert.That(ColumnTypeRegistry.Get<DateTime>().SupportedSettings).IsEqualTo(ColumnSettingKinds.Timestamp);
        await Assert.That(ColumnTypeRegistry.Get<DateTimeOffset>().SupportedSettings).IsEqualTo(ColumnSettingKinds.Timestamp);
        await Assert.That(ColumnTypeRegistry.Get<int>().SupportedSettings).IsEqualTo(ColumnSettingKinds.None);

        var integer = ColumnTypeRegistry.Get<int>();
        await Assert.That(integer.Configure(new ColumnSettings { Precision = 5 })).IsSameReferenceAs(integer);
    }

    [Test]
    [Arguments(9, 2, PhysicalType.Int32, null)]
    [Arguments(10, 2, PhysicalType.Int64, null)]
    [Arguments(18, 0, PhysicalType.Int64, null)]
    [Arguments(19, 2, PhysicalType.FixedLenByteArray, 9)]
    [Arguments(38, 18, PhysicalType.FixedLenByteArray, 16)]
    [Arguments(39, 0, PhysicalType.FixedLenByteArray, 17)]
    public async Task ADecimalIsStoredAsTheSmallestTypeThatHoldsItsPrecision(int precision, int scale, PhysicalType physical, int? length)
    {
        var node = ColumnTypeRegistry.Get<decimal>().Configure(new ColumnSettings { Precision = precision, Scale = scale }).CreateNode("d", FieldRepetitionType.Optional);

        await Assert.That(node.Type).IsEqualTo(physical);
        await Assert.That(node.TypeLength).IsEqualTo(length);
        await Assert.That(node.Repetition).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(node.LogicalType!.Precision).IsEqualTo(precision);
        await Assert.That(node.LogicalType!.Scale).IsEqualTo(scale);
    }

    [Test]
    public async Task ADecimalWithoutSettings_IsThirtyEightDigitsWithEighteenDecimals()
    {
        var node = ColumnTypeRegistry.Get<decimal>().CreateNode("d", FieldRepetitionType.Required);

        await Assert.That(node.LogicalType!.Precision).IsEqualTo(38);
        await Assert.That(node.LogicalType!.Scale).IsEqualTo(18);
    }

    [Test]
    public async Task TheSettingsOfTimesAndTimestamps_ChooseTheUnitAndThePhysicalType()
    {
        var millis = ColumnTypeRegistry.Get<TimeOnly>().Configure(new ColumnSettings { Unit = TimeUnit.Millis, IsAdjustedToUtc = true }).CreateNode("t", FieldRepetitionType.Required);
        var nanos = ColumnTypeRegistry.Get<TimeOnly>().Configure(new ColumnSettings { Unit = TimeUnit.Nanos }).CreateNode("t", FieldRepetitionType.Required);
        var defaultTime = ColumnTypeRegistry.Get<TimeOnly>().CreateNode("t", FieldRepetitionType.Required);
        var local = ColumnTypeRegistry.Get<DateTime>().Configure(new ColumnSettings { Unit = TimeUnit.Millis, IsAdjustedToUtc = false }).CreateNode("t", FieldRepetitionType.Required);
        var defaultTimestamp = ColumnTypeRegistry.Get<DateTimeOffset>().CreateNode("t", FieldRepetitionType.Required);

        await Assert.That(millis.Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(millis.LogicalType!.IsAdjustedToUtc).IsTrue();
        await Assert.That(nanos.Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(nanos.LogicalType!.Unit).IsEqualTo(TimeUnit.Nanos);
        await Assert.That(defaultTime.LogicalType!.Unit).IsEqualTo(TimeUnit.Micros);
        await Assert.That(defaultTime.LogicalType!.IsAdjustedToUtc).IsFalse();
        await Assert.That(local.LogicalType!.Unit).IsEqualTo(TimeUnit.Millis);
        await Assert.That(local.LogicalType!.IsAdjustedToUtc).IsFalse();
        await Assert.That(defaultTimestamp.LogicalType!.Unit).IsEqualTo(TimeUnit.Micros);
        await Assert.That(defaultTimestamp.LogicalType!.IsAdjustedToUtc).IsTrue();
    }

    [Test]
    public async Task AConfiguredColumnType_WritesAndReadsItsOwnColumn()
    {
        var type = ColumnTypeRegistry.Get<DateTime>().Configure(new ColumnSettings { Unit = TimeUnit.Millis, IsAdjustedToUtc = false });
        var values = new[] { new DateTime(2024, 5, 6, 7, 8, 9, 123), new DateTime(1999, 12, 31, 23, 59, 59) };
        var bytes = Write(type.CreateNode("t", FieldRepetitionType.Required), w => w.WriteBatch(type, values));

        var back = new DateTime[2];
        Open(bytes).ReadBatch(type, back);

        await Assert.That(back).IsEquivalentTo(values);
    }

    // ---- reading and writing with a given type ----

    [Test]
    public async Task ReadingAndWritingWithAType_IsWhatTheTypedOverloadsDo()
    {
        var node = new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(16, false));
        var bytes = Write(node, w => w.WriteBatch(ColumnTypeRegistry.Get<ushort>(), new ushort[] { 1, 2, 65535 }));

        var viaType = new ushort[3];
        var viaOverload = new ushort[3];
        Open(bytes).ReadBatch(ColumnTypeRegistry.Get<ushort>(), viaType);
        Open(bytes).ReadBatch(viaOverload);

        await Assert.That(viaType).IsEquivalentTo(new ushort[] { 1, 2, 65535 });
        await Assert.That(viaOverload).IsEquivalentTo(viaType);
    }

    [Test]
    public async Task ATypeIsRefusedAColumnItDoesNotFit_ForReadingAndWriting()
    {
        var node = new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Required);
        var bytes = Write(node, w => w.WriteBatch([1, 2, 3]));

        await Assert.That(() => Open(bytes).ReadBatch(ColumnTypeRegistry.Get<DateOnly>(), new DateOnly[3])).Throws<ArgumentException>();
        await Assert.That(() => Open(bytes).ReadBatch(ColumnTypeRegistry.Get<Guid>(), new Guid[3])).Throws<ArgumentException>();
        await Assert.That(() => Write(new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Required), w => w.WriteBatch(ColumnTypeRegistry.Get<string>(), ["a"]))).Throws<ArgumentException>();
        await Assert.That(() => Open(bytes).ReadBatch<int>(null!, new int[3])).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AnEnumColumn_IsTheNamesOfItsValues_AndAnUnknownNameIsRefused()
    {
        var type = ColumnTypeRegistry.Get<DayOfWeek>();
        var node = type.CreateNode("day", FieldRepetitionType.Required);
        var bytes = Write(node, w => w.WriteBatch(type, [DayOfWeek.Monday, DayOfWeek.Sunday, DayOfWeek.Monday]));

        var back = new DayOfWeek[3];
        Open(bytes).ReadBatch(type, back);
        var names = new string[3];
        Open(bytes).ReadStrings(names);
        var wrong = Write(new PrimitiveNode("day", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String), w => w.WriteStrings(["Funday"]));

        await Assert.That(node.LogicalType!.Kind).IsEqualTo(LogicalTypeKind.Enum);
        await Assert.That(back).IsEquivalentTo(new[] { DayOfWeek.Monday, DayOfWeek.Sunday, DayOfWeek.Monday });
        await Assert.That(names).IsEquivalentTo(new[] { "Monday", "Sunday", "Monday" });
        await Assert.That(() => Open(wrong).ReadBatch(type, new DayOfWeek[1])).Throws<ValueConversionException>();
    }

    // ---- an own column type ----

    public readonly record struct Money(long Cents);

    /// <summary>Money as a count of cents in an INT64 column, written the way a user of the library would.</summary>
    private sealed class MoneyColumnType : ColumnType<Money>
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

    [Test]
    public async Task AnOwnColumnType_ReadsAndWritesLikeABuiltInOne()
    {
        var type = new MoneyColumnType();
        var bytes = Write(type.CreateNode("price", FieldRepetitionType.Required), w => w.WriteBatch(type, [new Money(1), new Money(250), new Money(-3)]));

        var back = new Money[3];
        Open(bytes).ReadBatch(type, back);

        await Assert.That(type.Name).IsEqualTo("Money");
        await Assert.That(type.ValueType).IsEqualTo(typeof(Money));
        await Assert.That(back.Select(m => m.Cents)).IsEquivalentTo(new long[] { 1, 250, -3 });
        await Assert.That(() => Open(Write(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required), w => w.WriteBatch([1]))).ReadBatch(type, new Money[1]))
            .Throws<ArgumentException>();
    }

    public sealed class Invoice
    {
        public string Number { get; set; } = "";

        public Money Total { get; set; }

        public Money? Discount { get; set; }

        public List<Money> Lines { get; set; } = [];
    }

    [Test]
    public async Task ARegisteredColumnType_TeachesTheSerializerANewType()
    {
        ColumnTypeRegistry.Register(new MoneyColumnType());
        var invoices = new List<Invoice>
        {
            new() { Number = "A", Total = new Money(1000), Discount = new Money(50), Lines = [new Money(400), new Money(600)] },
            new() { Number = "B", Total = new Money(7), Discount = null, Lines = [] },
        };

        var stream = new MemoryStream();
        ParquetSerializer.Serialize(invoices, stream);
        stream.Position = 0;
        var back = ParquetSerializer.Deserialize<Invoice>(stream);

        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(back[0].Total).IsEqualTo(new Money(1000));
        await Assert.That(back[0].Discount).IsEqualTo(new Money(50));
        await Assert.That(back[0].Lines).IsEquivalentTo(new[] { new Money(400), new Money(600) });
        await Assert.That(back[1].Discount).IsNull();
        await Assert.That(back[1].Lines).IsEmpty();
        await Assert.That(ColumnTypeRegistry.IsSupported(typeof(Money))).IsTrue();
        await Assert.That(() => ColumnTypeRegistry.Register<Money>(null!)).Throws<ArgumentNullException>();
    }
}
