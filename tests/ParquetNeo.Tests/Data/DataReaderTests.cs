using System.Data;
using ParquetNeo.Core;
using ParquetNeo.Data;
using ParquetNeo.Tests.Core;
using ParquetNeo.Tests.Logical;

namespace ParquetNeo.Tests.Data;

public class DataReaderTests
{
    private static ParquetDataReader OpenFixture(string name, ParquetDataReaderOptions? options = null) =>
        new(new MemoryStream(File.ReadAllBytes(TestBytes.FixturePath(name))), (options ?? new ParquetDataReaderOptions()) with { Reader = new ParquetReaderOptions { Codecs = TestParquetFile.Registry } });

    // ---- types and values of files other tools wrote ----

    [Test]
    [Arguments("pyarrow-logical-required-plain.parquet", false)]
    [Arguments("pyarrow-logical-optional-plain.parquet", true)]
    [Arguments("pyarrow-logical-optional-dictionary.parquet", true)]
    [Arguments("pyarrow-logical-optional-v2-snappy.parquet", true)]
    public async Task ALogicalFileWrittenByPyArrow_IsReadAsTheTypesTheyStandFor(string fixture, bool optional)
    {
        var expectedTypes = new[]
        {
            typeof(sbyte), typeof(short), typeof(byte), typeof(ushort), typeof(uint), typeof(ulong), typeof(DateOnly), typeof(TimeOnly),
            typeof(TimeOnly), typeof(TimeOnly), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(Guid), typeof(Half),
        };

        using var reader = OpenFixture(fixture);

        await Assert.That(reader.FieldCount).IsEqualTo(LogicalDataset.Names.Length);
        for (int column = 0; column < reader.FieldCount; column++)
        {
            await Assert.That(reader.GetName(column)).IsEqualTo(LogicalDataset.Names[column]);
            await Assert.That(reader.GetFieldType(column)).IsEqualTo(expectedTypes[column]);
        }

        int row = 0;
        while (reader.Read())
        {
            for (int column = 0; column < reader.FieldCount; column++)
            {
                if (optional && LogicalDataset.IsNull(column, row))
                {
                    await Assert.That(reader.IsDBNull(column)).IsTrue();
                    await Assert.That(reader.GetValue(column)).IsEqualTo(DBNull.Value);
                }
                else
                {
                    await Assert.That(reader.GetValue(column)).IsEqualTo(LogicalDataset.ValueAt(column, row)).Because($"{LogicalDataset.Names[column]} row {row}");
                }
            }

            row++;
        }

        await Assert.That(row).IsEqualTo(LogicalDataset.RowCount);
    }

    [Test]
    [Arguments("pyarrow-decimal-required-plain.parquet", false)]
    [Arguments("pyarrow-decimal-optional-dictionary.parquet", true)]
    public async Task DecimalsAndText_AreReadAsDecimalAndString(string fixture, bool optional)
    {
        using var reader = OpenFixture(fixture);

        for (int column = 0; column < 5; column++)
        {
            await Assert.That(reader.GetFieldType(column)).IsEqualTo(typeof(decimal));
        }

        for (int column = 5; column < 8; column++)
        {
            await Assert.That(reader.GetFieldType(column)).IsEqualTo(typeof(string));
        }

        int row = 0;
        while (reader.Read())
        {
            for (int column = 0; column < reader.FieldCount; column++)
            {
                if (optional && DecimalTextDataset.IsNull(column, row))
                {
                    await Assert.That(reader.IsDBNull(column)).IsTrue();
                }
                else
                {
                    await Assert.That(reader.GetValue(column)).IsEqualTo(DecimalTextDataset.ValueAt(column, row)).Because($"{DecimalTextDataset.Names[column]} row {row}");
                }
            }

            row++;
        }

        await Assert.That(row).IsEqualTo(DecimalTextDataset.RowCount);
    }

    [Test]
    public async Task AFileWithManyPages_IsReadToTheEnd()
    {
        using var reader = OpenFixture("pyarrow-flat-optional-v1-smallpages.parquet", new ParquetDataReaderOptions { Columns = ["i32", "i64", "flag", "text", "blob", "day", "ts", "money", "small", "big"] });

        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        using var file = ReadHelper.OpenFixture("pyarrow-flat-optional-v1-smallpages.parquet");
        await Assert.That(rows).IsEqualTo((int)file.RowCount);
    }

    [Test]
    public async Task OnlyTheColumnsThatHoldOneValuePerRow_AreExposed()
    {
        using var reader = OpenFixture("golden-serializer-nested.parquet");

        await Assert.That(reader.FieldCount).IsEqualTo(1);
        await Assert.That(reader.GetName(0)).IsEqualTo("id");
        await Assert.That(reader.GetFieldType(0)).IsEqualTo(typeof(long));
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetInt64(0)).IsEqualTo(0L);
    }

    // ---- choosing columns ----

    [Test]
    public async Task TheColumnsOption_ChoosesTheColumnsAndTheirOrder()
    {
        var options = new ParquetDataReaderOptions { Columns = ["uint8", "int8"] };

        using var reader = OpenFixture("pyarrow-logical-required-plain.parquet", options);

        await Assert.That(reader.FieldCount).IsEqualTo(2);
        await Assert.That(reader.GetName(0)).IsEqualTo("uint8");
        await Assert.That(reader.GetName(1)).IsEqualTo("int8");
        reader.Read();
        await Assert.That(reader.GetByte(0)).IsEqualTo((byte)0);
        await Assert.That(reader.GetValue(1)).IsEqualTo((sbyte)-128);
    }

    [Test]
    public async Task AColumnThatIsNotThere_IsRefused()
    {
        var options = new ParquetDataReaderOptions { Columns = ["nope"] };

        var exception = Assert.Throws<ArgumentException>(() => OpenFixture("pyarrow-logical-required-plain.parquet", options));

        await Assert.That(exception.Message).Contains("'nope'");
    }

    // ---- the DbDataReader contract ----

    private static ParquetDataReader OpenSmall()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Data", typeof(byte[]));
        table.Columns.Add("Born", typeof(DateTime));
        table.Rows.Add(1, "Ada", new byte[] { 1, 2, 3, 4 }, new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        table.Rows.Add(2, DBNull.Value, DBNull.Value, DBNull.Value);
        var stream = new MemoryStream();
        ParquetDataWriter.Write(table.CreateDataReader(), stream);
        stream.Position = 0;
        return new ParquetDataReader(stream);
    }

    [Test]
    public async Task TheTypedGetters_GiveTheValues()
    {
        using var reader = OpenSmall();
        reader.Read();

        await Assert.That(reader.GetInt32(0)).IsEqualTo(1);
        await Assert.That(reader.GetString(1)).IsEqualTo("Ada");
        await Assert.That(reader.GetDateTime(3)).IsEqualTo(new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await Assert.That(reader.GetFieldValue<int>(0)).IsEqualTo(1);
        await Assert.That(reader["Name"]).IsEqualTo("Ada");
        await Assert.That(reader[0]).IsEqualTo(1);
        await Assert.That(reader.GetDataTypeName(1)).IsEqualTo("String");
    }

    [Test]
    public async Task GetValues_FillsAsManyAsFit()
    {
        using var reader = OpenSmall();
        reader.Read();
        var all = new object[4];
        var some = new object[2];

        await Assert.That(reader.GetValues(all)).IsEqualTo(4);
        await Assert.That(reader.GetValues(some)).IsEqualTo(2);
        await Assert.That(all[1]).IsEqualTo("Ada");
        await Assert.That(some[0]).IsEqualTo(1);
    }

    [Test]
    public async Task GetBytesAndGetChars_CopyInPieces()
    {
        using var reader = OpenSmall();
        reader.Read();
        var bytes = new byte[3];
        var chars = new char[5];

        await Assert.That(reader.GetBytes(2, 0, null, 0, 0)).IsEqualTo(4L);
        await Assert.That(reader.GetBytes(2, 2, bytes, 1, 2)).IsEqualTo(2L);
        await Assert.That(reader.GetBytes(2, 9, bytes, 0, 1)).IsEqualTo(0L);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0, 3, 4 });
        await Assert.That(reader.GetChars(1, 0, null, 0, 0)).IsEqualTo(3L);
        await Assert.That(reader.GetChars(1, 1, chars, 2, 2)).IsEqualTo(2L);
        await Assert.That(new string(chars, 2, 2)).IsEqualTo("da");
        await Assert.That(reader.GetChars(1, 5, chars, 0, 1)).IsEqualTo(0L);
    }

    [Test]
    public async Task ANull_IsDBNull_AndATypedGetterRefusesIt()
    {
        using var reader = OpenSmall();
        reader.Read();
        reader.Read();

        await Assert.That(reader.IsDBNull(0)).IsFalse();
        await Assert.That(reader.IsDBNull(1)).IsTrue();
        await Assert.That(reader.GetValue(1)).IsEqualTo(DBNull.Value);
        await Assert.That(() => reader.GetString(1)).Throws<InvalidCastException>();
        await Assert.That(() => reader.GetDateTime(3)).Throws<InvalidCastException>();
    }

    [Test]
    public async Task AGetterOfTheWrongType_IsAnInvalidCast()
    {
        using var reader = OpenSmall();
        reader.Read();

        await Assert.That(() => reader.GetInt64(0)).Throws<InvalidCastException>();
        await Assert.That(() => reader.GetString(0)).Throws<InvalidCastException>();
        await Assert.That(() => reader.GetChar(1)).Throws<InvalidCastException>();
    }

    [Test]
    public async Task OrdinalsAreFoundByName_ExactFirstThenIgnoringCase()
    {
        using var reader = OpenSmall();

        await Assert.That(reader.GetOrdinal("Name")).IsEqualTo(1);
        await Assert.That(reader.GetOrdinal("name")).IsEqualTo(1);
        await Assert.That(() => reader.GetOrdinal("missing")).Throws<IndexOutOfRangeException>();
        await Assert.That(() => reader.GetOrdinal(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ThereIsNoCurrentRow_BeforeReadAndAfterTheEnd()
    {
        using var reader = OpenSmall();

        await Assert.That(() => reader.GetValue(0)).Throws<InvalidOperationException>();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.Read()).IsFalse();
        await Assert.That(reader.Read()).IsFalse();
        await Assert.That(() => reader.GetValue(0)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AClosedReader_IsClosed()
    {
        var reader = OpenSmall();
        reader.Read();

        reader.Close();
        reader.Close();

        await Assert.That(reader.IsClosed).IsTrue();
        await Assert.That(() => reader.Read()).Throws<ObjectDisposedException>();
        await Assert.That(() => reader.GetValue(0)).Throws<ObjectDisposedException>();
        reader.Dispose();
    }

    [Test]
    public async Task TheFixedMembers_AreThoseOfASingleResultSet()
    {
        using var reader = OpenSmall();

        await Assert.That(reader.HasRows).IsTrue();
        await Assert.That(reader.Depth).IsEqualTo(0);
        await Assert.That(reader.RecordsAffected).IsEqualTo(-1);
        await Assert.That(reader.NextResult()).IsFalse();
        await Assert.That(reader.FieldCount).IsEqualTo(4);
    }

    [Test]
    public async Task TheSchemaTable_DescribesTheColumns()
    {
        using var reader = OpenFixture("pyarrow-decimal-optional-plain.parquet");

        var table = reader.GetSchemaTable();

        await Assert.That(table.Rows.Count).IsEqualTo(8);
        await Assert.That((string)table.Rows[0]["ColumnName"]).IsEqualTo("decimal_int32");
        await Assert.That((Type)table.Rows[0]["DataType"]).IsEqualTo(typeof(decimal));
        await Assert.That((short)table.Rows[0]["NumericPrecision"]).IsGreaterThan((short)0);
        await Assert.That((bool)table.Rows[0]["AllowDBNull"]).IsTrue();
        await Assert.That((int)table.Rows[7]["ColumnOrdinal"]).IsEqualTo(7);
    }

    [Test]
    public async Task ARequiredColumn_DoesNotAllowNull()
    {
        using var reader = OpenFixture("pyarrow-logical-required-plain.parquet");

        var table = reader.GetSchemaTable();

        await Assert.That((bool)table.Rows[0]["AllowDBNull"]).IsFalse();
    }

    [Test]
    public async Task TheReader_CanBeEnumeratedAndLoadedIntoADataTable()
    {
        using var reader = OpenSmall();
        var table = new DataTable();

        table.Load(reader);

        await Assert.That(table.Rows.Count).IsEqualTo(2);
        await Assert.That(table.Columns["Born"]!.DataType).IsEqualTo(typeof(DateTime));
        await Assert.That(table.Rows[0]["Name"]).IsEqualTo("Ada");
        await Assert.That(table.Rows[1]["Name"]).IsEqualTo(DBNull.Value);
    }

    [Test]
    public async Task ReadAsync_WorksLikeRead()
    {
        using var reader = OpenSmall();

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(await reader.ReadAsync()).IsFalse();
    }

    [Test]
    public async Task ANonSeekableOrUnreadableStream_IsRefused()
    {
        await Assert.That(() => new ParquetDataReader(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ParquetDataReader(new MemoryStream(new byte[1], writable: false))).Throws<Exception>();
    }

    [Test]
    public async Task LeaveOpen_KeepsTheStream()
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(int));
        table.Rows.Add(1);
        var stream = new MemoryStream();
        ParquetDataWriter.Write(table.CreateDataReader(), stream);
        stream.Position = 0;

        using (var reader = new ParquetDataReader(stream, leaveOpen: true))
        {
            reader.Read();
        }

        await Assert.That(stream.CanRead).IsTrue();
    }
}
