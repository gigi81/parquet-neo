using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.ColumnTypes;

public class BinaryColumnTypeTests
{
    private static readonly ColumnType<byte[]> Binary = ColumnTypeRegistry.Get<byte[]>();

    private static PrimitiveNode Fixed(int length, FieldRepetitionType repetition = FieldRepetitionType.Required, LogicalType? logical = null) =>
        new("hash", PhysicalType.FixedLenByteArray, repetition, logical, typeLength: length);

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

    [Test]
    public async Task AnUnannotatedFixedLengthColumn_FitsByteArrays()
    {
        await Assert.That(Binary.GetProblem(Fixed(16))).IsNull();
        await Assert.That(Binary.GetProblem(Fixed(1))).IsNull();
    }

    [Test]
    public async Task AnAnnotatedFixedLengthColumn_StillBelongsToItsOwnType()
    {
        await Assert.That(Binary.GetProblem(Fixed(16, logical: LogicalType.Uuid))).IsNotNull();
        await Assert.That(Binary.GetProblem(Fixed(2, logical: LogicalType.Float16))).IsNotNull();
        await Assert.That(Binary.GetProblem(Fixed(8, logical: LogicalType.Decimal(2, 15)))).IsNotNull();
    }

    [Test]
    public async Task TheColumnItCreates_IsStillAByteArray()
    {
        var node = Binary.CreateNode("data", FieldRepetitionType.Optional);

        await Assert.That(node.Type).IsEqualTo(PhysicalType.ByteArray);
    }

    [Test]
    public async Task FixedLengthValues_RoundTripAsByteArrays()
    {
        var values = new[] { new byte[] { 1, 2, 3, 4 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 255, 254, 253, 252 } };
        var bytes = Write(Fixed(4), w => w.WriteBatch(Binary, values));

        var back = new byte[3][];
        Open(bytes).ReadBatch(Binary, back);

        await Assert.That(back.Length).IsEqualTo(3);
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(back[i]).IsEquivalentTo(values[i]);
        }

        // The result is a copy: the arrays are the caller's.
        await Assert.That(back[0]).IsNotSameReferenceAs(values[0]);
    }

    [Test]
    public async Task Nulls_AndSeveralBatches_AreRead()
    {
        var present = new[] { new byte[] { 1, 1 }, new byte[] { 2, 2 }, new byte[] { 3, 3 } };
        int[] levels = [1, 0, 1, 1, 0];
        var bytes = Write(Fixed(2, FieldRepetitionType.Optional), w => w.WriteBatch(Binary, present, levels));

        using var reader = Open(bytes);
        var first = new byte[3][];
        var firstLevels = new int[3];
        var read1 = reader.ReadBatch(Binary, first, firstLevels);
        var second = new byte[3][];
        var secondLevels = new int[3];
        var read2 = reader.ReadBatch(Binary, second, secondLevels);

        await Assert.That(read1.Entries).IsEqualTo(3);
        await Assert.That(read1.Values).IsEqualTo(2);
        await Assert.That(firstLevels.Take(3)).IsEquivalentTo(new[] { 1, 0, 1 });
        await Assert.That(first[0]).IsEquivalentTo(present[0]);
        await Assert.That(first[1]).IsEquivalentTo(present[1]);
        await Assert.That(read2.Entries).IsEqualTo(2);
        await Assert.That(read2.Values).IsEqualTo(1);
        await Assert.That(second[0]).IsEquivalentTo(present[2]);
    }

    [Test]
    public async Task AValueOfAnotherLength_CannotBeWritten()
    {
        var node = Fixed(4);

        await Assert.That(() => Write(node, w => w.WriteBatch(Binary, [new byte[] { 1, 2, 3 }]))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write(Fixed(4), w => w.WriteBatch(Binary, [new byte[] { 1, 2, 3, 4, 5 }]))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write(Fixed(4), w => w.WriteBatch(Binary, [(byte[])null!]))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ByteArrayColumns_WorkAsBefore()
    {
        var node = new PrimitiveNode("data", PhysicalType.ByteArray, FieldRepetitionType.Required);
        var values = new[] { new byte[] { 1 }, Array.Empty<byte>(), new byte[] { 2, 3, 4 } };
        var bytes = Write(node, w => w.WriteBatch(Binary, values));

        var back = new byte[3][];
        Open(bytes).ReadBatch(Binary, back);

        await Assert.That(back[0]).IsEquivalentTo(values[0]);
        await Assert.That(back[1]).IsEmpty();
        await Assert.That(back[2]).IsEquivalentTo(values[2]);
    }

    [Test]
    public async Task AFixedLengthColumnOfAFileWrittenByPyArrow_IsReadLikeThePhysicalOverload()
    {
        using var file = ReadHelper.OpenFixture("pyarrow-flat-optional-v1-smallpages.parquet");
        int column = file.Schema.Columns.ToList().FindIndex(c => c.Name == "fixed");
        var expected = ReadHelper.ReadColumn(file, 0, column, 7);

        using var reader = file.OpenRowGroup(0).OpenColumn(column);
        var result = new List<byte[]?>();
        var batch = new byte[7][];
        var levels = new int[7];
        while (true)
        {
            var read = reader.ReadBatch(Binary, batch, levels);
            if (read.Entries == 0)
            {
                break;
            }

            int next = 0;
            for (int entry = 0; entry < read.Entries; entry++)
            {
                result.Add(levels[entry] == 0 ? null : batch[next++]);
            }
        }

        await Assert.That(result.Count).IsEqualTo(expected.Length);
        await Assert.That(result.Count).IsGreaterThan(0);
        for (int row = 0; row < expected.Length; row++)
        {
            if (expected[row] is null)
            {
                await Assert.That(result[row]).IsNull();
            }
            else
            {
                await Assert.That(result[row]).IsEquivalentTo((byte[])expected[row]!);
            }
        }
    }

    public sealed class Hashed
    {
        public int Id { get; set; }

        public byte[] Hash { get; set; } = [];

        public byte[]? Optional { get; set; }
    }

    [Test]
    public async Task TheSerializer_ReadsAFixedLengthColumnIntoAByteArrayProperty()
    {
        var schema = new ParquetSchema(
        [
            new PrimitiveNode("Id", PhysicalType.Int32, FieldRepetitionType.Required),
            new PrimitiveNode("Hash", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, typeLength: 3),
            new PrimitiveNode("Optional", PhysicalType.FixedLenByteArray, FieldRepetitionType.Optional, typeLength: 2),
        ]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, options: null, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2]);
            group.NextColumn().WriteBatch(Binary, [new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }]);
            group.NextColumn().WriteBatch(Binary, [new byte[] { 9, 9 }], [0, 1]);
        }

        stream.Position = 0;
        var rows = ParquetSerializer.Deserialize<Hashed>(stream);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].Hash).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rows[1].Hash).IsEquivalentTo(new byte[] { 4, 5, 6 });
        await Assert.That(rows[0].Optional).IsNull();
        await Assert.That(rows[1].Optional).IsEquivalentTo(new byte[] { 9, 9 });
    }
}
