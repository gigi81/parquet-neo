using ParquetNeo.Serialization.Assemblers;

namespace ParquetNeo.Tests.Serialization.Assemblers;

public class ItemScratchTests
{
    [Test]
    public async Task ItemsAreCollectedInOrder_BeyondTheRoomItStartsWith()
    {
        var scratch = new ItemScratch<int>();

        for (int i = 0; i < 1000; i++)
        {
            scratch.Add(i * 3);
        }

        await Assert.That(scratch.Count).IsEqualTo(1000);
        await Assert.That(scratch.Items.ToArray()).IsEquivalentTo(Enumerable.Range(0, 1000).Select(i => i * 3).ToArray());
    }

    [Test]
    public async Task ItGrowsExactlyWhereItsRoomRunsOut()
    {
        var scratch = new ItemScratch<int>();

        // 16 items fit; the 17th is the first that needs more room.
        for (int i = 0; i < 16; i++)
        {
            scratch.Add(i);
        }

        await Assert.That(scratch.Items.ToArray()).IsEquivalentTo(Enumerable.Range(0, 16).ToArray());
        scratch.Add(16);
        await Assert.That(scratch.Items.ToArray()).IsEquivalentTo(Enumerable.Range(0, 17).ToArray());
    }

    [Test]
    public async Task ClearStartsOver_AndKeepsItsRoom()
    {
        var scratch = new ItemScratch<string>();
        scratch.Add("a");
        scratch.Add("b");

        scratch.Clear();
        scratch.Add("c");

        await Assert.That(scratch.Count).IsEqualTo(1);
        await Assert.That(scratch.Items.ToArray()).IsEquivalentTo(["c"]);
    }

    [Test]
    public async Task ReleaseLetsGoOfTheItems_ForTypesThatHoldReferences()
    {
        var scratch = new ItemScratch<object>();
        var weak = Add(scratch);

        scratch.Release();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Assert.That(scratch.Count).IsEqualTo(0);
        await Assert.That(weak.IsAlive).IsFalse();
    }

    // Not inlined, so that nothing on the stack of the test still refers to the object.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference Add(ItemScratch<object> scratch)
    {
        var item = new object();
        scratch.Add(item);
        return new WeakReference(item);
    }

    [Test]
    public async Task ReleaseOfValuesJustResetsTheCount()
    {
        var scratch = new ItemScratch<double>();
        scratch.Add(1.5);
        scratch.Add(2.5);

        scratch.Release();
        scratch.Add(9.5);

        await Assert.That(scratch.Items.ToArray()).IsEquivalentTo([9.5]);
    }
}
