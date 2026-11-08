using ParquetNeo.Core;

namespace ParquetNeo.Tests.Core;

public class PooledBufferWriterTests
{
    [Test]
    public async Task WhatIsWritten_IsKeptWhileTheBufferGrows()
    {
        using var writer = new PooledBufferWriter<int>();

        for (int i = 0; i < 100_000; i++)
        {
            writer.Append(i);
        }

        await Assert.That(writer.WrittenCount).IsEqualTo(100_000);
        await Assert.That(writer.WrittenSpan.Slice(99_990, 3).ToArray()).IsEquivalentTo(new[] { 99_990, 99_991, 99_992 });
        await Assert.That(writer.WrittenSpan[0]).IsEqualTo(0);
    }

    [Test]
    public async Task GetSpanAndAdvance_WorkLikeAnArrayBufferWriter()
    {
        using var writer = new PooledBufferWriter<byte>();

        int firstLength = WriteAndAdvance(writer, 10, 1, 2);
        int secondLength = WriteAndAdvance(writer, 5000, 3);

        await Assert.That(firstLength).IsGreaterThanOrEqualTo(10);
        await Assert.That(secondLength).IsGreaterThanOrEqualTo(5000);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(writer.GetMemory(4).Length).IsGreaterThanOrEqualTo(4);
    }

    /// <summary>Asks for room for <paramref name="hint"/> bytes, writes the values to its start and returns how much room it got.</summary>
    private static int WriteAndAdvance(PooledBufferWriter<byte> writer, int hint, params byte[] values)
    {
        var span = writer.GetSpan(hint);
        values.CopyTo(span);
        writer.Advance(values.Length);
        return span.Length;
    }

    [Test]
    public async Task ResetWrittenCount_EmptiesTheBufferAndKeepsItsRoom()
    {
        using var writer = new PooledBufferWriter<byte>();
        WriteAndAdvance(writer, 1000, new byte[1000]);

        writer.ResetWrittenCount();

        await Assert.That(writer.WrittenCount).IsEqualTo(0);
        await Assert.That(writer.WrittenSpan.IsEmpty).IsTrue();
        await Assert.That(writer.GetMemory(1000).Length).IsGreaterThanOrEqualTo(1000);
    }

    [Test]
    public async Task AWriter_CanBeUsedAgain_AfterItIsDisposed()
    {
        var writer = new PooledBufferWriter<int>();
        writer.Append(1);
        writer.Dispose();
        writer.Dispose();

        writer.Append(2);

        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(new[] { 2 });
        writer.Dispose();
    }

    [Test]
    public async Task BadArguments_AreRefused()
    {
        using var writer = new PooledBufferWriter<int>();
        writer.Append(1);

        await Assert.That(() => writer.Advance(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => writer.Advance(int.MaxValue)).Throws<InvalidOperationException>();
        await Assert.That(() => writer.GetSpan(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => writer.GetMemory(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AWriterThatWasNeverUsed_IsEmpty()
    {
        using var writer = new PooledBufferWriter<byte>();

        await Assert.That(writer.WrittenCount).IsEqualTo(0);
        await Assert.That(writer.WrittenSpan.Length).IsEqualTo(0);
    }
}
