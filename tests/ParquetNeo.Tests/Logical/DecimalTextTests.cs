using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Logical;

public class DecimalTextTests
{
    private static async Task AssertDataset(byte[] bytes, bool optional, string where)
    {
        using var reader = ReadHelper.Open(bytes);
        for (int column = 0; column < DecimalTextDataset.Names.Length; column++)
        {
            foreach (int batch in new[] { 1, 7, 100 })
            {
                using var columnReader = reader.OpenRowGroup(0).OpenColumn(column);
                var actual = DecimalTextDataset.Read(columnReader, column, batch);

                await Assert.That(actual.Length).IsEqualTo(DecimalTextDataset.RowCount).Because($"{where} {DecimalTextDataset.Names[column]}");
                for (int row = 0; row < actual.Length; row++)
                {
                    object? expected = optional && DecimalTextDataset.IsNull(column, row) ? null : DecimalTextDataset.ValueAt(column, row);
                    await Assert.That(actual[row]).IsEqualTo(expected).Because($"{where} {DecimalTextDataset.Names[column]} row {row} batch {batch}");
                }
            }
        }
    }

    [Test]
    [Arguments("pyarrow-decimal-required-plain.parquet", false)]
    [Arguments("pyarrow-decimal-optional-plain.parquet", true)]
    [Arguments("pyarrow-decimal-optional-dictionary.parquet", true)]
    [Arguments("pyarrow-decimal-optional-v2-snappy.parquet", true)]
    public async Task ThePyArrowFiles_ReadAsDecimalsAndStrings(string fixture, bool optional)
    {
        await AssertDataset(File.ReadAllBytes(TestBytes.FixturePath(fixture)), optional, fixture);
    }

    [Test]
    public async Task ThePyArrowFiles_StoreDecimalsAsEveryPhysicalType()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-decimal-required-plain.parquet");

        await Assert.That(reader.Schema.Columns.Take(5).Select(c => c.Type)).IsEquivalentTo(new[]
        {
            PhysicalType.Int32, PhysicalType.Int64, PhysicalType.FixedLenByteArray, PhysicalType.FixedLenByteArray, PhysicalType.FixedLenByteArray,
        });
        await Assert.That(reader.Schema.Columns[3].TypeLength).IsEqualTo(17); // wider than the 16 bytes of an Int128
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OurFiles_RoundTrip_AndCarryTheLogicalTypes(bool optional)
    {
        var bytes = DecimalTextDataset.Write(optional);

        await AssertDataset(bytes, optional, "round trip");
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        var expected = DecimalTextDataset.Schema(optional);
        for (int column = 0; column < DecimalTextDataset.Names.Length; column++)
        {
            await Assert.That(file.Schema.Columns[column].LogicalType).IsEqualTo(expected.Columns[column].LogicalType);
            await Assert.That(file.Schema.Columns[column].ConvertedType).IsEqualTo(expected.Columns[column].ConvertedType);
        }

        await Assert.That(file.Schema.Columns.Take(5).Select(c => c.ConvertedType)).IsEquivalentTo(Enumerable.Repeat<ConvertedType?>(ConvertedType.Decimal, 5));
        await Assert.That(file.Schema.Columns.Skip(5).Select(c => c.ConvertedType)).IsEquivalentTo(new ConvertedType?[] { ConvertedType.Utf8, ConvertedType.Enum, ConvertedType.Json });
    }

    [Test]
    public async Task TheWriterOutput_MatchesTheGoldenFile_ByteForByte()
    {
        var actual = DecimalTextDataset.Write(optional: true, new ParquetWriterOptions { CreatedBy = "ParquetNeo golden" });

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-decimal.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-decimal.parquet"))));
    }

    [Test]
    public async Task ByteArrayDecimals_AreStoredInTheirShortestForm()
    {
        var node = new PrimitiveNode("d", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.Decimal(2, 38));
        var bytes = Write(node, w => w.WriteBatch([0m, 1.27m, 1.28m, -1.28m, -1.29m, 0.01m, 12345678901234567890.12m]));

        var arrays = new ReadOnlyMemory<byte>[7];
        Open(bytes).ReadByteArrays(arrays);

        string big = Convert.ToHexString(System.Numerics.BigInteger.Parse("1234567890123456789012").ToByteArray(isUnsigned: false, isBigEndian: true));
        await Assert.That(arrays.Select(a => Convert.ToHexString(a.Span))).IsEquivalentTo(new[] { "00", "7F", "0080", "80", "FF7F", "01", big });
    }

    // ---- decimals the types or columns cannot hold ----

    private static byte[] Write(PrimitiveNode node, Action<ColumnWriter> write, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            write(group.NextColumn());
        }

        return stream.ToArray();
    }

    private static ColumnReader Open(byte[] bytes, int column = 0) => ReadHelper.Open(bytes).OpenRowGroup(0).OpenColumn(column);

    private static PrimitiveNode Decimal(PhysicalType type, int scale, int precision, int? length = null) =>
        new("d", type, FieldRepetitionType.Required, LogicalType.Decimal(scale, precision), typeLength: length);

    [Test]
    public async Task DecimalsAreWrittenAndRead_AtTheScaleOfTheColumn_WhateverTheirOwnScale()
    {
        foreach (var node in new[]
                 {
                     Decimal(PhysicalType.Int32, 2, 9), Decimal(PhysicalType.Int64, 2, 18), Decimal(PhysicalType.FixedLenByteArray, 2, 20, 9), Decimal(PhysicalType.ByteArray, 2, 20),
                 })
        {
            var bytes = Write(node, w => w.WriteBatch([1.5m, 1.50m, 1.500m, 2m, -0.07m]));
            var back = new decimal[5];
            Open(bytes).ReadBatch(back);

            await Assert.That(back).IsEquivalentTo(new[] { 1.5m, 1.5m, 1.5m, 2m, -0.07m }).Because(node.Type.ToString());
            // Whatever the writer was given, the reader gets the column's scale.
            await Assert.That(back.Select(v => (decimal.GetBits(v)[3] >> 16) & 0xFF)).IsEquivalentTo(Enumerable.Repeat(2, 5)).Because(node.Type.ToString());
        }
    }

    [Test]
    public async Task ADecimalThatWouldLoseDigits_OrExceedThePrecision_IsRejectedWhenWriting()
    {
        // The node and the first value more than it holds, per physical type.
        foreach (var (node, tooMany) in new[]
                 {
                     (Decimal(PhysicalType.Int32, 2, 9), 10_000_000.00m),
                     (Decimal(PhysicalType.Int64, 2, 18), 10_000_000_000_000_000.00m),
                     (Decimal(PhysicalType.FixedLenByteArray, 2, 9, 4), 10_000_000.00m),
                     (Decimal(PhysicalType.ByteArray, 2, 9), 10_000_000.00m),
                 })
        {
            var stream = new MemoryStream();
            using var file = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true);
            using var group = file.AppendRowGroup();
            var column = group.NextColumn();

            await Assert.That(() => column.WriteBatch([0.001m])).Throws<ArgumentOutOfRangeException>().Because($"{node.Type} loses a digit");
            await Assert.That(() => column.WriteBatch([tooMany])).Throws<ArgumentOutOfRangeException>().Because($"{node.Type} exceeds the precision");
            await Assert.That(() => column.WriteBatch([-tooMany])).Throws<ArgumentOutOfRangeException>().Because($"{node.Type} exceeds the precision when negative");
        }
    }

    [Test]
    public async Task StoredDecimalsTooLargeForANetDecimal_AreReported()
    {
        // 2^96, a 29-digit number: a precision-38 FLBA(16) column can store it, a .NET decimal cannot hold it.
        var node = Decimal(PhysicalType.FixedLenByteArray, 0, 38, 16);
        var bytes = Write(node, w => w.WriteFixedLengthByteArrays(Convert.FromHexString(new string('0', 7) + "1" + new string('0', 24))));

        await Assert.That(() => Open(bytes).ReadBatch(new decimal[1])).Throws<ValueConversionException>();
    }

    [Test]
    public async Task AColumnThatIsNotADecimal_OrHasTooLargeAScale_IsRejected_ByBothDirections()
    {
        var plain = new PrimitiveNode("d", PhysicalType.Int32, FieldRepetitionType.Required);
        var date = new PrimitiveNode("d", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Date);
        var scale29 = Decimal(PhysicalType.FixedLenByteArray, 29, 38, 16);
        var floating = new PrimitiveNode("d", PhysicalType.Double, FieldRepetitionType.Required);

        foreach (var node in new[] { plain, date, scale29, floating })
        {
            var stream = new MemoryStream();
            using var file = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true);
            using var group = file.AppendRowGroup();
            var column = group.NextColumn();

            await Assert.That(() => column.WriteBatch([1m])).Throws<ArgumentException>().Because($"{node.Type} {node.LogicalType?.Kind}");
        }

        using var reader = ReadHelper.OpenFixture("pyarrow-decimal-required-plain.parquet");
        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn("text").ReadBatch(new decimal[1])).Throws<ArgumentException>();
        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn("decimal_int32").ReadBatch(new decimal[1], [1])).Throws<ArgumentException>(); // required: no levels
        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn("decimal_int32").ReadBatch(new long[1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ADecimalColumnWithTheLargestScale_OfADecimal_CanBeUsed()
    {
        var node = Decimal(PhysicalType.FixedLenByteArray, 28, 38, 16);
        var bytes = Write(node, w => w.WriteBatch([7.9228162514264337593543950335m, -0.0000000000000000000000000001m, 0m]));
        var back = new decimal[3];
        Open(bytes).ReadBatch(back);

        await Assert.That(back).IsEquivalentTo(new[] { 7.9228162514264337593543950335m, -0.0000000000000000000000000001m, 0m });
    }

    [Test]
    public async Task Decimals_RoundTripAtEveryScale_ThroughFixedAndVariableLengthBytes()
    {
        var random = new Random(11);
        for (int scale = 0; scale <= 28; scale++)
        {
            var values = new decimal[60];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new decimal(random.Next(), random.Next(), random.Next(0, 2) == 0 ? 0 : random.Next(), random.Next(2) == 0, (byte)scale);
            }

            foreach (var node in new[] { Decimal(PhysicalType.FixedLenByteArray, scale, 38, 16), Decimal(PhysicalType.FixedLenByteArray, scale, 31, 13), Decimal(PhysicalType.ByteArray, scale, 38) })
            {
                var bytes = Write(node, w => w.WriteBatch(values));
                var back = new decimal[values.Length];
                int total = 0;
                using var reader = Open(bytes);
                while (total < values.Length)
                {
                    total += reader.ReadBatch(back.AsSpan(total)).Values;
                }

                await Assert.That(back).IsEquivalentTo(values).Because($"scale {scale} {node.Type}");
            }
        }
    }

    [Test]
    public async Task Decimals_OfEveryPhysicalType_RoundTripWithLevelsAcrossPages()
    {
        var random = new Random(3);
        var values = Enumerable.Range(0, 5000).Select(i => new decimal(random.Next(1_000_000), 0, 0, i % 3 == 0, 3)).ToArray();
        var levels = Enumerable.Range(0, values.Length + 700).Select(i => i % 8 == 0 ? 0 : 1).ToArray();
        var present = new decimal[levels.Count(l => l == 1)];
        Array.Copy(values, present, present.Length);

        foreach (var type in new[] { PhysicalType.Int32, PhysicalType.Int64, PhysicalType.FixedLenByteArray, PhysicalType.ByteArray })
        {
            var node = new PrimitiveNode("d", type, FieldRepetitionType.Optional, LogicalType.Decimal(3, 9), typeLength: type == PhysicalType.FixedLenByteArray ? 5 : null);
            var bytes = Write(node, w => w.WriteBatch(present, levels), new ParquetWriterOptions { DataPageSize = 700 });

            await Assert.That(TestParquetFile.Parse(bytes).DataPages(0, 0).Count).IsGreaterThan(3).Because(type.ToString());
            using var reader = Open(bytes);
            var back = new List<decimal>();
            var buffer = new decimal[333];
            var batchLevels = new int[333];
            BatchRead read;
            while ((read = reader.ReadBatch(buffer, batchLevels)).Entries > 0)
            {
                back.AddRange(buffer.Take(read.Values));
            }

            await Assert.That(back).IsEquivalentTo(present).Because(type.ToString());
        }
    }

    // ---- text ----

    [Test]
    public async Task Strings_AreReadFromUnannotatedColumns_ButNotFromOtherLogicalTypes()
    {
        var unannotated = new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required);
        var bytes = Write(unannotated, w => w.WriteStrings(["a", "b"]));
        var back = new string[2];
        Open(bytes).ReadStrings(back);

        await Assert.That(back).IsEquivalentTo(new[] { "a", "b" });

        foreach (var logical in new[] { LogicalType.Bson, LogicalType.Decimal(0, 10) })
        {
            var node = new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, logical);
            var stream = new MemoryStream();
            using var file = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true);
            using var group = file.AppendRowGroup();
            var column = group.NextColumn();

            await Assert.That(() => column.WriteStrings(["x"])).Throws<ArgumentException>().Because(logical.Kind.ToString());
        }

        using var reader = ReadHelper.OpenFixture("pyarrow-decimal-required-plain.parquet");
        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn("decimal_bytes").ReadStrings(new string[1])).Throws<ArgumentException>();
        await Assert.That(() => reader.OpenRowGroup(0).OpenColumn("decimal_int32").ReadStrings(new string[1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task BytesThatAreNotUtf8_AreReported_WithTheColumnAndTheCause()
    {
        var node = new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);
        var bytes = Write(node, w => w.WriteByteArrays([new byte[] { 0x61 }, new byte[] { 0xC3, 0x28 }, new byte[] { 0xED, 0xA0, 0x80 }]));

        var exception = Assert.Throws<ValueConversionException>(() => Open(bytes).ReadStrings(new string[3]));

        await Assert.That(exception.Message).Contains("name");
        await Assert.That(exception.InnerException).IsNotNull();
    }

    [Test]
    public async Task InvalidUtf8_ThatIsLongOrSurrogate_IsReported()
    {
        foreach (var invalid in new byte[][] { [0xFF], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE2, 0x82] })
        {
            var node = new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);
            var bytes = Write(node, w => w.WriteByteArrays([invalid]));

            await Assert.That(() => Open(bytes).ReadStrings(new string[1])).Throws<ValueConversionException>().Because(Convert.ToHexString(invalid));
        }
    }

    [Test]
    public async Task Strings_SpanManyPages_AndBatchesEndAtPageBoundaries()
    {
        var node = new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String);
        var values = Enumerable.Range(0, 3000).Select(i => $"value {i} é日" + new string('x', i % 40)).ToArray();
        var bytes = Write(node, w => w.WriteStrings(values), new ParquetWriterOptions { DataPageSize = 1000, DictionaryEnabled = false });

        await Assert.That(TestParquetFile.Parse(bytes).DataPages(0, 0).Count).IsGreaterThan(10);
        using var reader = Open(bytes);
        var back = new List<string>();
        var buffer = new string[1000];
        BatchRead read;
        while ((read = reader.ReadStrings(buffer)).Entries > 0)
        {
            back.AddRange(buffer.Take(read.Values));
        }

        await Assert.That(back).IsEquivalentTo(values);
    }

    [Test]
    public async Task Strings_FromADictionaryColumn_AreRead()
    {
        var node = new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.Enum);
        var values = Enumerable.Range(0, 500).Select(i => new[] { "RED", "GREEN", "BLUE" }[i % 3]).ToArray();
        var bytes = Write(node, w => w.WriteStrings(values));

        await Assert.That(TestParquetFile.Parse(bytes).Pages(0, 0)[0].Header.Type).IsEqualTo(PageType.DictionaryPage);
        var back = new string[500];
        Open(bytes).ReadStrings(back);

        await Assert.That(back).IsEquivalentTo(values);
    }
}
