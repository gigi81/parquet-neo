using System.Data;
using ParquetNeo.Data;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Data;

/// <summary>The ADO.NET writer encodes a row group while the one before is written asynchronously, and writes straight to the stream synchronously.</summary>
public class AsyncDataWriterTests
{
    private static DataTable Table(int rows)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("score", typeof(double));
        for (int i = 0; i < rows; i++)
        {
            table.Rows.Add((long)i, i % 5 == 0 ? DBNull.Value : "name-" + (i % 50), i % 3 == 0 ? DBNull.Value : i * 0.5);
        }

        return table;
    }

    private static readonly ParquetDataWriterOptions Options = new() { RowGroupSize = 700 };

    private static byte[] Sync(DataTable table)
    {
        var stream = new TestDestination();
        ParquetDataWriter.Write(table.CreateDataReader(), stream, Options);
        return stream.Bytes;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(700)]
    [Arguments(2_000)]
    [Arguments(5_000)]
    public async Task TheAsynchronousFile_IsTheSameAsTheSynchronousOne(int rows)
    {
        var table = Table(rows);
        var destination = new TestDestination { Delay = TimeSpan.FromMilliseconds(1) };

        await ParquetDataWriter.WriteAsync(table.CreateDataReader(), destination, Options);

        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(Sync(table))).IsTrue();
        await Assert.That(destination.MaxInFlight).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task TheSynchronousWriter_WritesStraightToTheStream_AFileThatReadsBack()
    {
        var table = Table(3_000);
        var destination = new TestDestination();

        ParquetDataWriter.Write(table.CreateDataReader(), destination, Options);

        using var reader = new ParquetDataReader(new MemoryStream(destination.Bytes));
        int rows = 0;
        while (reader.Read())
        {
            await Assert.That(reader.GetInt64(0)).IsEqualTo((long)rows);
            rows++;
        }

        await Assert.That(rows).IsEqualTo(3_000);
    }

    [Test]
    public async Task AWriteThatFails_ThrowsItsException_AndNothingIsLeftWriting()
    {
        var destination = new TestDestination { FailOnWrite = 1 };

        await Assert.ThrowsAsync<IOException>(async () => await ParquetDataWriter.WriteAsync(Table(4_000).CreateDataReader(), destination, Options));

        await Assert.That(destination.InFlight).IsEqualTo(0);
    }
}
