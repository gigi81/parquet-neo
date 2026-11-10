using System.Data;
using ParquetNeo.Data;

namespace ParquetNeo.Tests.Data;

/// <summary>The buffers of the ADO.NET writer take their arrays from the pool; these check that what the pool left in them does not show.</summary>
public class ColumnBufferTests
{
    private static DataTable Table(int rows, Func<int, object?[]> row)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("score", typeof(double));
        for (int i = 0; i < rows; i++)
        {
            table.Rows.Add(row(i));
        }

        return table;
    }

    private static byte[] Write(DataTable table, int rowGroupSize = 4_000)
    {
        var stream = new MemoryStream();
        ParquetDataWriter.Write(table.CreateDataReader(), stream, new ParquetDataWriterOptions { RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    private static object?[] Junk(int i) => [long.MaxValue - i, "junk-" + i, -1e300];

    private static object?[] Sparse(int i) => [(long)i, i % 10 == 0 ? "name-" + i : DBNull.Value, i % 4 == 0 ? i * 0.5 : DBNull.Value];

    [Test]
    public async Task TheSameRows_MakeTheSameBytes_NoMatterWhatThePoolHeldBefore()
    {
        var rows = Table(12_000, Sparse);
        var first = Write(rows);
        for (int round = 0; round < 3; round++)
        {
            Write(Table(30_000, Junk));
        }

        var second = Write(Table(12_000, Sparse));

        await Assert.That(second.AsSpan().SequenceEqual(first)).IsTrue();
    }

    [Test]
    public async Task ValuesAndNullsRoundTrip_AcrossRowGroupsThatReuseTheBuffers()
    {
        var table = Table(10_500, Sparse);
        var bytes = Write(table, rowGroupSize: 1_000);

        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        int rows = 0;
        while (reader.Read())
        {
            var expected = Sparse(rows);
            await Assert.That(reader.GetInt64(0)).IsEqualTo((long)expected[0]!);
            await Assert.That(reader.IsDBNull(1)).IsEqualTo(expected[1] is DBNull);
            await Assert.That(reader.IsDBNull(2)).IsEqualTo(expected[2] is DBNull);
            if (expected[1] is string name)
            {
                await Assert.That(reader.GetString(1)).IsEqualTo(name);
            }

            rows++;
        }

        await Assert.That(rows).IsEqualTo(10_500);
    }

    [Test]
    public async Task ANullInARequiredBuffer_IsRefusedWithTheRowAndAnOptionalOneTakesIt()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();
        using var required = new ColumnBuffer<long>(0, "id", ParquetNeo.ColumnTypes.ColumnTypeRegistry.Get<long>(), optional: false);
        using var optional = new ColumnBuffer<long>(0, "id", ParquetNeo.ColumnTypes.ColumnTypeRegistry.Get<long>(), optional: true);

        var exception = Assert.Throws<InvalidOperationException>(() => required.Add(reader, 5));

        await Assert.That(exception.Message).Contains("Column 'id' is not nullable");
        await Assert.That(exception.Message).Contains("row 5");
        await Assert.That(() => optional.Add(reader, 5)).ThrowsNothing();
    }
}
