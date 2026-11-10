using ParquetNeo.Core;

namespace ParquetNeo.Tests.Core;

public class StagingStreamTests
{
    [Test]
    public async Task TakeGivesTheBytesWritten_AndAnEmptyBufferForTheNext()
    {
        using var stream = new StagingStream();
        stream.Write([1, 2, 3], 0, 3);
        stream.Write([4, 5]);

        using var first = stream.Take();
        stream.Write([9]);
        using var second = stream.Take();

        await Assert.That(first.Memory.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5 });
        await Assert.That(second.Memory.ToArray()).IsEquivalentTo(new byte[] { 9 });
    }

    [Test]
    public async Task WhatWasTakenIsNotChangedByWhatIsWrittenAfterwards()
    {
        using var stream = new StagingStream();
        stream.Write(new byte[1000]);
        using var taken = stream.Take();

        // Write enough to need the room of the first buffer twice over: it must be another buffer.
        stream.Write(Enumerable.Repeat((byte)7, 5000).ToArray());

        await Assert.That(taken.Memory.ToArray().All(b => b == 0)).IsTrue();
        await Assert.That(taken.Memory.Length).IsEqualTo(1000);
    }

    [Test]
    public async Task LengthAndPositionAreWhatWasWrittenSinceTheLastTake()
    {
        using var stream = new StagingStream();
        stream.Write(new byte[10]);

        await Assert.That(stream.Length).IsEqualTo(10L);
        await Assert.That(stream.Position).IsEqualTo(10L);
        stream.Take().Dispose();
        await Assert.That(stream.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task ItIsAWriteOnlyStreamThatCannotSeek()
    {
        using var stream = new StagingStream();

        await Assert.That(stream.CanWrite).IsTrue();
        await Assert.That(stream.CanRead).IsFalse();
        await Assert.That(stream.CanSeek).IsFalse();
        await Assert.That(() => stream.Read(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Seek(0, SeekOrigin.Begin)).Throws<NotSupportedException>();
        await Assert.That(() => stream.SetLength(0)).Throws<NotSupportedException>();
        await Assert.That(() => stream.Position = 0).Throws<NotSupportedException>();
        await Assert.That(() => stream.Flush()).ThrowsNothing();
    }

    [Test]
    public async Task TakingNothing_GivesNothing()
    {
        using var stream = new StagingStream();

        using var nothing = stream.Take();

        await Assert.That(nothing.Memory.Length).IsEqualTo(0);
    }
}
