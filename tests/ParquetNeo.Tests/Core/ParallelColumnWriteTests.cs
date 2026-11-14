using ParquetNeo.Compression;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class ParallelColumnWriteTests
{
    private const int Rows = 20_000;

    private static readonly ParquetSchema Schema = new(
    [
        new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
        new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Optional),
        new PrimitiveNode("count", PhysicalType.Int32, FieldRepetitionType.Required),
    ]);

    private static readonly long[] Ids = Enumerable.Range(0, Rows).Select(i => (long)i * 7).ToArray();
    private static readonly int[] Counts = Enumerable.Range(0, Rows).Select(i => i % 13).ToArray();
    private static readonly int[] NameLevels = Enumerable.Range(0, Rows).Select(i => i % 5 == 0 ? 0 : 1).ToArray();
    private static readonly string[] Names = Enumerable.Range(0, Rows).Where(i => i % 5 != 0).Select(i => "name-" + (i % 300)).ToArray();
    private static readonly int[] ScoreLevels = Enumerable.Range(0, Rows).Select(i => i % 3 == 0 ? 0 : 1).ToArray();
    private static readonly double[] Scores = Enumerable.Range(0, Rows).Where(i => i % 3 != 0).Select(i => i * 0.5).ToArray();

    private static readonly Action<ColumnWriter>[] Actions =
    [
        column => column.WriteBatch(Ids),
        column => column.WriteStrings(Names, NameLevels),
        column => column.WriteBatch(Scores, ScoreLevels),
        column => column.WriteBatch(Counts),
    ];

    private static ParquetWriterOptions Options(CompressionCodec codec = CompressionCodec.Uncompressed) =>
        new() { Compression = codec, Codecs = TestParquetFile.Registry, CreatedBy = "test" };

    private static byte[] OneColumnAtATime(int groups, CompressionCodec codec)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, Schema, Options(codec), leaveOpen: true))
        {
            for (int g = 0; g < groups; g++)
            {
                using var group = writer.AppendRowGroup();
                foreach (var action in Actions)
                {
                    action(group.NextColumn());
                }
            }
        }

        return stream.ToArray();
    }

    private static byte[] AllAtOnce(int groups, CompressionCodec codec, Action<RowGroupWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, Schema, Options(codec), leaveOpen: true))
        {
            for (int g = 0; g < groups; g++)
            {
                using var group = writer.AppendRowGroup();
                write(group);
            }
        }

        return stream.ToArray();
    }

    [Test]
    [Arguments(1, CompressionCodec.Uncompressed)]
    [Arguments(2, CompressionCodec.Uncompressed)]
    [Arguments(4, CompressionCodec.Snappy)]
    [Arguments(16, CompressionCodec.Gzip)]
    public async Task TheFile_IsTheSameAsWritingOneColumnAtATime_WhateverTheDegree(int degree, CompressionCodec codec)
    {
        var expected = OneColumnAtATime(groups: 3, codec);

        var actual = AllAtOnce(groups: 3, codec, group => group.WriteColumns(Actions, degree));

        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
        TestParquetFile.Parse(actual).AssertStructure();
    }

    [Test]
    public async Task WithoutADegree_ItUsesTheProcessors_AndMakesTheSameFile()
    {
        var expected = OneColumnAtATime(groups: 2, CompressionCodec.Uncompressed);

        var actual = AllAtOnce(groups: 2, CompressionCodec.Uncompressed, group => group.WriteColumns(Actions));

        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task TheColumns_AreEncodedAtTheSameTime_UpToTheDegree()
    {
        // Each of the three actions waits for the others: that only gets anywhere if they run together.
        using var barrier = new Barrier(3);
        bool together = true;
        var waiting = Enumerable.Range(0, 3).Select(index => (Action<ColumnWriter>)(column =>
        {
            together &= barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            column.WriteBatch(Ids);
        })).ToArray();
        var schema = new ParquetSchema(Enumerable.Range(0, 3).Select(i => new PrimitiveNode("c" + i, PhysicalType.Int64, FieldRepetitionType.Required)).ToArray());
        using var writer = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();

        group.WriteColumns(waiting, maxDegreeOfParallelism: 3);

        await Assert.That(together).IsTrue();
    }

    [Test]
    public async Task NoMoreColumnsThanTheDegreeRunAtOnce()
    {
        int running = 0;
        int most = 0;
        var counting = Enumerable.Range(0, 4).Select(index => (Action<ColumnWriter>)(column =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
            {
            }

            Thread.Sleep(50);
            Interlocked.Decrement(ref running);
            column.WriteBatch(Ids);
        })).ToArray();
        var schema = new ParquetSchema(Enumerable.Range(0, 4).Select(i => new PrimitiveNode("c" + i, PhysicalType.Int64, FieldRepetitionType.Required)).ToArray());
        using var writer = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();

        group.WriteColumns(counting, maxDegreeOfParallelism: 2);

        await Assert.That(most).IsLessThanOrEqualTo(2);
        await Assert.That(most).IsGreaterThan(0);
    }

    [Test]
    public async Task BadArguments_AreRefusedBeforeAnythingIsWritten()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), Schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();

        await Assert.That(() => group.WriteColumns(null!, 2)).Throws<ArgumentNullException>();
        await Assert.That(() => group.WriteColumns(Actions, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => group.WriteColumns([Actions[0], null!, Actions[2], Actions[3]], 2)).Throws<ArgumentException>();
        await Assert.That(() => group.WriteColumns(Actions[..3], 2)).Throws<InvalidOperationException>();

        // Nothing was started by any of that: the group can still be written.
        group.WriteColumns(Actions, 2);
    }

    [Test]
    public async Task AGroupThatHasStarted_IsRefused()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), Schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();
        Actions[0](group.NextColumn());

        await Assert.That(() => group.WriteColumns(Actions, 2)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AnActionThatThrows_ThrowsFromHere_AndWritesNothingOfTheGroup()
    {
        var stream = new MemoryStream();
        using var writer = new ParquetFileWriter(stream, Schema, leaveOpen: true);
        long before = stream.Length;
        var group = writer.AppendRowGroup();
        Action<ColumnWriter>[] failing = [Actions[0], _ => throw new FormatException("the second column"), Actions[2], Actions[3]];

        var exception = Assert.Throws<FormatException>(() => group.WriteColumns(failing, 4));
        group.Dispose();

        await Assert.That(exception.Message).IsEqualTo("the second column");
        await Assert.That(stream.Length).IsEqualTo(before);
        await Assert.That(() => writer.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ColumnsWithDifferentRowCounts_AreRefused()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), Schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();
        Action<ColumnWriter>[] uneven = [Actions[0], Actions[1], Actions[2], column => column.WriteBatch(Counts.AsSpan(0, 10))];

        await Assert.That(() => group.WriteColumns(uneven, 2)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ACancelledToken_StopsTheGroup()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var writer = new ParquetFileWriter(new MemoryStream(), Schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();

        await Assert.That(() => group.WriteColumns(Actions, 2, cancellation.Token)).Throws<OperationCanceledException>();
    }
}
