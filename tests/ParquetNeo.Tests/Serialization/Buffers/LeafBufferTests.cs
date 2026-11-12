using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization.Buffers;

public class LeafBufferTests
{
    private static readonly ParquetSchema OptionalLong = new([new PrimitiveNode("c", PhysicalType.Int64, FieldRepetitionType.Optional)]);

    private static byte[] Write(params LeafBuffer<long>[] groups)
    {
        var stream = new MemoryStream();
        using var writer = new ParquetFileWriter(stream, OptionalLong, leaveOpen: true);
        foreach (var buffer in groups)
        {
            using var group = writer.AppendRowGroup();
            buffer.WriteAndClear(group.NextColumn());
        }

        writer.Close();
        return stream.ToArray();
    }

    private static LeafBuffer<long> Buffer(int capacity) => new(hasDefinitionLevels: true, hasRepetitionLevels: false, ColumnTypeRegistry.Get<long>(), capacity);

    [Test]
    public async Task ValuesAndLevels_GrowBeyondTheRoomItStartedWith()
    {
        using var buffer = Buffer(capacity: 2);
        var expected = new List<long?>();
        for (int i = 0; i < 5000; i++)
        {
            if (i % 3 == 0)
            {
                buffer.AddMissing(0, 0);
                expected.Add(null);
            }
            else
            {
                buffer.AddValue(i * 11L, 1, 0);
                expected.Add(i * 11L);
            }
        }

        var file = TestParquetFile.Parse(Write(buffer));

        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task AfterWriting_TheBufferIsEmptyAndTakesTheNextRowGroup()
    {
        using var buffer = Buffer(capacity: 4);
        for (int i = 0; i < 10; i++)
        {
            buffer.AddValue(1000 + i, 1, 0);
        }

        // The first group is written from the buffer; what the second group holds must not include any of it.
        var first = new MemoryStream();
        using (var writer = new ParquetFileWriter(first, OptionalLong, leaveOpen: true))
        {
            using (var group = writer.AppendRowGroup())
            {
                buffer.WriteAndClear(group.NextColumn());
            }

            buffer.AddMissing(0, 0);
            buffer.AddValue(7, 1, 0);
            using (var group = writer.AppendRowGroup())
            {
                buffer.WriteAndClear(group.NextColumn());
            }

            writer.Close();
        }

        var file = TestParquetFile.Parse(first.ToArray());

        await Assert.That(file.ReadColumn<long>(0, 0)).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => (long?)(1000 + i)));
        await Assert.That(file.ReadColumn<long>(1, 0)).IsEquivalentTo(new long?[] { null, 7 });
    }

    [Test]
    public async Task DisposingTwice_IsHarmless()
    {
        var buffer = Buffer(capacity: 8);
        buffer.AddValue(1, 1, 0);

        await Assert.That(() =>
        {
            buffer.Dispose();
            buffer.Dispose();
        }).ThrowsNothing();
    }

    private sealed class Row
    {
        public long Id { get; set; }

        public string? Text { get; set; }

        public double? Score { get; set; }

        public List<int?>? Numbers { get; set; }
    }

    private static byte[] Serialize(IEnumerable<Row> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    [Test]
    public async Task ArraysTheyPoolLeftDirty_DoNotShowInTheNextFile()
    {
        // Fill the pool with arrays full of values, then write rows that are mostly nulls and empty lists into the same arrays.
        for (int round = 0; round < 3; round++)
        {
            Serialize(Enumerable.Range(0, 50_000).Select(i => new Row { Id = long.MaxValue - i, Text = "junk-" + i, Score = -i, Numbers = [i, i, i] }));
        }

        var sparse = Enumerable.Range(0, 50_000).Select(i => new Row { Id = i, Text = i % 100 == 0 ? "t" + i : null, Score = i % 50 == 0 ? i : null, Numbers = i % 7 == 0 ? [i] : null }).ToArray();
        var bytes = Serialize(sparse);

        var read = ParquetSerializer.Deserialize<Row>(new MemoryStream(bytes));
        await Assert.That(read.Select(r => r.Id)).IsEquivalentTo(sparse.Select(r => r.Id));
        await Assert.That(read.Select(r => r.Text)).IsEquivalentTo(sparse.Select(r => r.Text));
        await Assert.That(read.Select(r => r.Score)).IsEquivalentTo(sparse.Select(r => r.Score));
        await Assert.That(read.Select(r => r.Numbers is null ? "null" : string.Join(',', r.Numbers))).IsEquivalentTo(sparse.Select(r => r.Numbers is null ? "null" : string.Join(',', r.Numbers)));
    }

    [Test]
    public async Task TheSameRows_MakeTheSameBytes_NoMatterWhatTheArraysHeldBefore()
    {
        var rows = Enumerable.Range(0, 20_000).Select(i => new Row { Id = i, Text = i % 3 == 0 ? null : "text-" + (i % 40), Score = i * 0.5, Numbers = [i % 5, null] }).ToArray();
        var first = Serialize(rows);
        Serialize(Enumerable.Range(0, 70_000).Select(i => new Row { Id = -i, Text = "other-" + i, Score = 1e300, Numbers = [-1, -2, -3, -4] }));

        var second = Serialize(rows);

        await Assert.That(second.AsSpan().SequenceEqual(first)).IsTrue();
    }

    [Test]
    public async Task TheStringsOfARowGroup_AreNotKeptAliveByThePool()
    {
        var weak = SerializeUniqueStrings();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Assert.That(weak.IsAlive).IsFalse();
    }

    // Not inlined, so that nothing on the stack of the test still refers to the rows.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SerializeUniqueStrings()
    {
        var marker = new string('x', 100);
        var rows = new[] { new Row { Id = 1, Text = marker }, new Row { Id = 2, Text = "y" } };
        Serialize(rows);
        return new WeakReference(marker);
    }
}
