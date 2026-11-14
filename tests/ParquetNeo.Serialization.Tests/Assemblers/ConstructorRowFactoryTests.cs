using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Serialization.Tests.Assemblers;

public class ConstructorRowFactoryTests
{
    private static ConstructorRowFactory<string> Joiner(int slots) =>
        new(slots, values => string.Join('|', values.Select(v => v ?? "-")));

    [Test]
    public async Task Begin_MakesRoomForEverySlot_AndEndBuildsFromTheValues()
    {
        var factory = Joiner(3);
        var context = new RowContext();

        factory.Begin(context);
        context.Slots![0] = "a";
        context.Slots[2] = "c";

        await Assert.That(context.Slots.Length).IsEqualTo(3);
        await Assert.That(factory.End(context)).IsEqualTo("a|-|c");
    }

    [Test]
    public async Task Begin_ClearsTheValuesOfTheRowBefore_InTheSameArray()
    {
        var factory = Joiner(2);
        var context = new RowContext();
        factory.Begin(context);
        var slots = context.Slots;
        context.Slots![0] = "left over";
        context.Slots[1] = "left over";

        factory.Begin(context);

        await Assert.That(context.Slots).IsSameReferenceAs(slots);
        await Assert.That(factory.End(context)).IsEqualTo("-|-");
    }
}
