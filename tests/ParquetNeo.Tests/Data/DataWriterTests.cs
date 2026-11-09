using System.Data;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Data;
using ParquetNeo.Metadata;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Data;

public class DataWriterTests
{
    private static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("flag", typeof(bool));
        table.Columns.Add("tiny", typeof(byte));
        table.Columns.Add("small", typeof(short));
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("big", typeof(long));
        table.Columns.Add("single", typeof(float));
        table.Columns.Add("price", typeof(double));
        table.Columns.Add("amount", typeof(decimal));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("born", typeof(DateTime));
        table.Columns.Add("uid", typeof(Guid));
        table.Columns.Add("data", typeof(byte[]));
        table.Columns.Add("day", typeof(DateOnly));
        table.Columns.Add("signed", typeof(sbyte));
        table.Rows.Add(true, (byte)1, (short)-2, 3, 4_000_000_000L, 1.5f, 2.25, 12.34m, "Ada", Utc(1815, 12, 10), Guid.Parse("123e4567-e89b-12d3-a456-426614174000"), new byte[] { 1, 2, 3 }, new DateOnly(1815, 12, 10), (sbyte)-5);
        table.Rows.Add(false, (byte)255, short.MinValue, int.MinValue, long.MinValue, -0.5f, double.MaxValue, -0.01m, "", Utc(1912, 6, 23), Guid.Empty, Array.Empty<byte>(), new DateOnly(1912, 6, 23), sbyte.MaxValue);
        table.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        table.Rows.Add(true, (byte)7, (short)8, 9, 10L, 11f, 12d, 13m, "Grace", Utc(1906, 12, 9), Guid.NewGuid(), new byte[] { 255 }, new DateOnly(1906, 12, 9), (sbyte)0);
        return table;
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 1, 2, 3, DateTimeKind.Utc);

    private static byte[] Write(IDataReader source, ParquetDataWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetDataWriter.Write(source, stream, options);
        return stream.ToArray();
    }

    [Test]
    public async Task EveryColumnType_RoundTrips_NullsIncluded()
    {
        var table = CreateTable();

        var bytes = Write(table.CreateDataReader());

        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        await Assert.That(reader.FieldCount).IsEqualTo(table.Columns.Count);
        int row = 0;
        while (reader.Read())
        {
            for (int column = 0; column < reader.FieldCount; column++)
            {
                await Assert.That(reader.GetName(column)).IsEqualTo(table.Columns[column].ColumnName);
                await Assert.That(reader.GetFieldType(column)).IsEqualTo(table.Columns[column].DataType);
                object expected = table.Rows[row][column];
                if (expected is byte[] expectedBytes)
                {
                    await Assert.That((byte[])reader.GetValue(column)).IsEquivalentTo(expectedBytes);
                }
                else
                {
                    await Assert.That(reader.GetValue(column)).IsEqualTo(expected).Because($"row {row} column {reader.GetName(column)}");
                }
            }

            row++;
        }

        await Assert.That(row).IsEqualTo(table.Rows.Count);
    }

    [Test]
    public async Task TheColumnsAreOptional_UnlessTheReaderSaysTheyCannotBeNull()
    {
        var table = new DataTable();
        table.Columns.Add("required", typeof(int)).AllowDBNull = false;
        table.Columns.Add("optional", typeof(int));
        table.Rows.Add(1, 2);

        var bytes = Write(table.CreateDataReader());

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.Schema.Columns[0].Repetition).IsEqualTo(FieldRepetitionType.Required);
        await Assert.That(file.Schema.Columns[1].Repetition).IsEqualTo(FieldRepetitionType.Optional);
    }

    [Test]
    public async Task ARequiredColumn_RefusesANullAndNamesTheRow()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Rows.Add(1);
        table.Rows.Add(DBNull.Value);
        using var source = table.CreateDataReader();
        source.Read();
        source.Read();
        var buffer = new ColumnBuffer<int>(0, "id", ColumnTypeRegistry.Get<int>(), optional: false);

        var exception = Assert.Throws<InvalidOperationException>(() => buffer.Add(source, 41));

        await Assert.That(exception.Message).Contains("'id'").And.Contains("row 41");
    }

    [Test]
    public async Task RowsAreWrittenInRowGroupsOfTheRequestedSize()
    {
        var table = CreateTable();

        var bytes = Write(table.CreateDataReader(), new ParquetDataWriterOptions { RowGroupSize = 3 });

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.RowGroupCount).IsEqualTo(2);
        await Assert.That(file.OpenRowGroup(0).RowCount).IsEqualTo(3);
        await Assert.That(file.OpenRowGroup(1).RowCount).IsEqualTo(1);
        await Assert.That(file.RowCount).IsEqualTo(4);
    }

    [Test]
    public async Task ManyRows_AcrossManyRowGroups_ComeBackInOrder()
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(long));
        table.Columns.Add("text", typeof(string));
        for (long i = 0; i < 25_000; i++)
        {
            table.Rows.Add(i, i % 5 == 0 ? DBNull.Value : "value " + (i % 97));
        }

        var bytes = Write(table.CreateDataReader(), new ParquetDataWriterOptions { RowGroupSize = 4_000 });

        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        long expected = 0;
        while (reader.Read())
        {
            await Assert.That(reader.GetInt64(0)).IsEqualTo(expected);
            await Assert.That(reader.IsDBNull(1)).IsEqualTo(expected % 5 == 0);
            if (expected % 5 != 0)
            {
                await Assert.That(reader.GetString(1)).IsEqualTo("value " + (expected % 97));
            }

            expected++;
        }

        await Assert.That(expected).IsEqualTo(25_000);
    }

    [Test]
    public async Task TheAsynchronousWriter_WritesTheSameFile()
    {
        var table = CreateTable();
        var asynchronous = new MemoryStream();

        await ParquetDataWriter.WriteAsync(table.CreateDataReader(), asynchronous);

        await Assert.That(asynchronous.ToArray()).IsEquivalentTo(Write(table.CreateDataReader()));
    }

    [Test]
    public async Task AnEmptyResult_IsAValidFileWithNoRows()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));

        var bytes = Write(table.CreateDataReader());

        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        await Assert.That(reader.HasRows).IsFalse();
        await Assert.That(reader.Read()).IsFalse();
        await Assert.That(reader.FieldCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnlyTheRestOfTheResultIsWritten()
    {
        var table = CreateTable();
        using var source = table.CreateDataReader();
        source.Read();
        source.Read();

        var bytes = Write(source);

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.RowCount).IsEqualTo(2);
    }

    [Test]
    public async Task ADecimal_IsStoredWith38And18_UnlessTheOptionsSayOtherwise()
    {
        var table = new DataTable();
        table.Columns.Add("amount", typeof(decimal));
        table.Columns.Add("fee", typeof(decimal));
        table.Rows.Add(1.5m, 0.25m);
        var settings = new Dictionary<string, ColumnSettings> { ["fee"] = new() { Precision = 9, Scale = 2 } };

        var bytes = Write(table.CreateDataReader(), new ParquetDataWriterOptions { ColumnSettings = settings });

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.Schema.Columns[0].LogicalType!.Precision).IsEqualTo(38);
        await Assert.That(file.Schema.Columns[0].LogicalType!.Scale).IsEqualTo(18);
        await Assert.That(file.Schema.Columns[1].Type).IsEqualTo(PhysicalType.Int32);
        await Assert.That(file.Schema.Columns[1].LogicalType!.Precision).IsEqualTo(9);
        await Assert.That(file.Schema.Columns[1].LogicalType!.Scale).IsEqualTo(2);
        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        reader.Read();
        await Assert.That(reader.GetDecimal(0)).IsEqualTo(1.5m);
        await Assert.That(reader.GetDecimal(1)).IsEqualTo(0.25m);
    }

    [Test]
    public async Task ADecimalTakesItsPrecisionAndScaleFromTheSchemaTable()
    {
        var table = new DataTable();
        table.Columns.Add("amount", typeof(decimal));
        table.Rows.Add(12.34m);
        using var source = new SchemaTableReader(table.CreateDataReader(), precision: 12, scale: 3);

        var bytes = Write(source);

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.Schema.Columns[0].LogicalType!.Precision).IsEqualTo(12);
        await Assert.That(file.Schema.Columns[0].LogicalType!.Scale).IsEqualTo(3);
    }

    [Test]
    public async Task ATimestampTakesTheUnitFromTheOptions()
    {
        var table = new DataTable();
        table.Columns.Add("moment", typeof(DateTime));
        table.Rows.Add(Utc(2024, 2, 29));
        var settings = new Dictionary<string, ColumnSettings> { ["moment"] = new() { Unit = TimeUnit.Millis, IsAdjustedToUtc = false } };

        var bytes = Write(table.CreateDataReader(), new ParquetDataWriterOptions { ColumnSettings = settings });

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.Schema.Columns[0].LogicalType!.Unit).IsEqualTo(TimeUnit.Millis);
        await Assert.That(file.Schema.Columns[0].LogicalType!.IsAdjustedToUtc).IsFalse();
    }

    [Test]
    public async Task AColumnOfAnUnsupportedType_IsRefusedByName()
    {
        var table = new DataTable();
        table.Columns.Add("elapsed", typeof(TimeSpan));
        table.Rows.Add(TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<NotSupportedException>(() => Write(table.CreateDataReader()));

        await Assert.That(exception.Message).Contains("'elapsed'").And.Contains("TimeSpan");
    }

    [Test]
    public async Task BadArguments_AreRefused()
    {
        var table = CreateTable();

        await Assert.That(() => ParquetDataWriter.Write(null!, new MemoryStream())).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetDataWriter.Write(table.CreateDataReader(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetDataWriter.Write(table.CreateDataReader(), new MemoryStream(new byte[1], writable: false))).Throws<ArgumentException>();
        await Assert.That(() => Write(table.CreateDataReader(), new ParquetDataWriterOptions { RowGroupSize = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write(new DataTable().CreateDataReader())).Throws<ArgumentException>();
    }

    [Test]
    public async Task TheWriterOptions_ReachTheFile()
    {
        var table = CreateTable();

        var bytes = Write(table.CreateDataReader(), new ParquetDataWriterOptions { Writer = new ParquetWriterOptions { Compression = CompressionCodec.Gzip } });

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.Metadata.RowGroups[0].Columns[0].MetaData!.Codec).IsEqualTo(CompressionCodec.Gzip);
        using var reader = new ParquetDataReader(new MemoryStream(bytes));
        await Assert.That(reader.Read()).IsTrue();
    }
}
