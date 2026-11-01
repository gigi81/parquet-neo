using System.Buffers.Binary;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Metadata;

/// <summary>
/// Parses files written by pyarrow (see tests/fixtures/generate_pyarrow_fixtures.py) to check the
/// metadata layer against a real, independent writer.
/// </summary>
public class PyArrowFixtureTests
{
    private const int TopLevelColumns = 19;
    private const int LeafColumns = 21;

    [Test]
    [Arguments("pyarrow-v1.parquet")]
    [Arguments("pyarrow-v2.parquet")]
    public async Task Footer_ParsesAndReserializesToIdenticalBytes(string fixture)
    {
        var footer = ReadFooterBytes(File.ReadAllBytes(TestBytes.FixturePath(fixture)));

        var metadata = FileMetaData.Deserialize(footer);

        await Assert.That(TestBytes.ToHex(metadata.ToBytes())).IsEqualTo(TestBytes.ToHex(footer));
    }

    [Test]
    [Arguments("pyarrow-v1.parquet")]
    [Arguments("pyarrow-v2.parquet")]
    public async Task Footer_ExposesFileAndSchemaInformation(string fixture)
    {
        var metadata = Parse(fixture);

        await Assert.That(metadata.NumRows).IsEqualTo(5L);
        await Assert.That(metadata.RowGroups.Count).IsEqualTo(2);
        await Assert.That(metadata.CreatedBy!).StartsWith("parquet-cpp-arrow");
        await Assert.That(metadata.Schema[0].NumChildren).IsEqualTo(TopLevelColumns);
        await Assert.That(metadata.ColumnOrders!.Count).IsEqualTo(LeafColumns);
        await Assert.That(metadata.RowGroups[0].NumRows).IsEqualTo(3L);
        await Assert.That(metadata.RowGroups[1].NumRows).IsEqualTo(2L);
        await Assert.That(metadata.RowGroups[0].Columns.Count).IsEqualTo(LeafColumns);
    }

    [Test]
    public async Task Schema_ReportsLogicalTypesAndLegacyConvertedTypes()
    {
        var schema = Parse("pyarrow-v1.parquet").Schema;

        var name = Find(schema, "name");
        await Assert.That(name.Type).IsEqualTo(PhysicalType.ByteArray);
        await Assert.That(name.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(name.ConvertedType).IsEqualTo(ConvertedType.Utf8);
        await Assert.That(name.RepetitionType).IsEqualTo(FieldRepetitionType.Optional);

        var amount = Find(schema, "amount");
        await Assert.That(amount.LogicalType).IsEqualTo(LogicalType.Decimal(scale: 2, precision: 10));
        await Assert.That(amount.ConvertedType).IsEqualTo(ConvertedType.Decimal);
        await Assert.That(amount.Scale).IsEqualTo(2);
        await Assert.That(amount.Precision).IsEqualTo(10);

        await Assert.That(Find(schema, "date").LogicalType).IsEqualTo(LogicalType.Date);
        await Assert.That(Find(schema, "int8").LogicalType).IsEqualTo(LogicalType.Integer(8, isSigned: true));
        await Assert.That(Find(schema, "uint16").LogicalType).IsEqualTo(LogicalType.Integer(16, isSigned: false));
        await Assert.That(Find(schema, "ts_ms").LogicalType)
            .IsEqualTo(LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Millis));
        await Assert.That(Find(schema, "ts_us_local").LogicalType)
            .IsEqualTo(LogicalType.Timestamp(isAdjustedToUtc: false, TimeUnit.Micros));
        await Assert.That(Find(schema, "time").LogicalType)
            .IsEqualTo(LogicalType.Time(isAdjustedToUtc: false, TimeUnit.Micros));

        var half = Find(schema, "half");
        await Assert.That(half.Type).IsEqualTo(PhysicalType.FixedLenByteArray);
        await Assert.That(half.TypeLength).IsEqualTo(2);
        await Assert.That(half.LogicalType).IsEqualTo(LogicalType.Float16);

        var fixedColumn = Find(schema, "fixed");
        await Assert.That(fixedColumn.TypeLength).IsEqualTo(4);
        await Assert.That(fixedColumn.LogicalType).IsNull();
    }

    [Test]
    public async Task Schema_ReportsNestedGroups()
    {
        var schema = Parse("pyarrow-v1.parquet").Schema;

        var tags = Find(schema, "tags");
        await Assert.That(tags.LogicalType).IsEqualTo(LogicalType.List);
        await Assert.That(tags.NumChildren).IsEqualTo(1);
        await Assert.That(tags.Type).IsNull();

        var point = Find(schema, "point");
        await Assert.That(point.NumChildren).IsEqualTo(2);
        await Assert.That(point.LogicalType).IsNull();

        await Assert.That(Find(schema, "attrs").LogicalType).IsEqualTo(LogicalType.Map);
    }

    [Test]
    [Arguments("pyarrow-v1.parquet")]
    [Arguments("pyarrow-v2.parquet")]
    public async Task ColumnChunk_ExposesStatisticsAndOffsets(string fixture)
    {
        var metadata = Parse(fixture);
        var id = metadata.RowGroups[0].Columns[0].MetaData!;

        await Assert.That(id.PathInSchema).IsEquivalentTo(new[] { "id" });
        await Assert.That(id.Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(id.Codec).IsEqualTo(CompressionCodec.Snappy);
        await Assert.That(id.NumValues).IsEqualTo(3L);
        await Assert.That(id.DictionaryPageOffset).IsEqualTo(4L);
        await Assert.That(id.DataPageOffset > id.DictionaryPageOffset).IsTrue();
        await Assert.That(id.Encodings).Contains(ParquetEncoding.RleDictionary);
        await Assert.That(id.Statistics!.NullCount).IsEqualTo(0L);
        await Assert.That(BinaryPrimitives.ReadInt64LittleEndian(id.Statistics.MinValue!)).IsEqualTo(1L);
        await Assert.That(BinaryPrimitives.ReadInt64LittleEndian(id.Statistics.MaxValue!)).IsEqualTo(3L);

        var name = metadata.RowGroups[0].Columns[7].MetaData!;
        await Assert.That(name.PathInSchema).IsEquivalentTo(new[] { "name" });
        await Assert.That(name.Statistics!.NullCount).IsEqualTo(1L);
        await Assert.That(System.Text.Encoding.UTF8.GetString(name.Statistics.MinValue!)).IsEqualTo("a");
        await Assert.That(System.Text.Encoding.UTF8.GetString(name.Statistics.MaxValue!)).IsEqualTo("bb");
    }

    [Test]
    [Arguments("pyarrow-v1.parquet", PageType.DataPage)]
    [Arguments("pyarrow-v2.parquet", PageType.DataPageV2)]
    public async Task EveryPageHeader_CanBeWalkedAcrossAllColumnChunks(string fixture, PageType expectedDataPageType)
    {
        var file = File.ReadAllBytes(TestBytes.FixturePath(fixture));
        var metadata = FileMetaData.Deserialize(ReadFooterBytes(file));
        int chunks = 0;
        int dictionaryPages = 0;

        foreach (var chunk in metadata.RowGroups.SelectMany(g => g.Columns))
        {
            var meta = chunk.MetaData!;
            long start = meta.DictionaryPageOffset ?? meta.DataPageOffset;
            long end = start + meta.TotalCompressedSize;
            long position = start;
            long values = 0;

            while (position < end)
            {
                var header = PageHeader.Deserialize(file.AsSpan((int)position), out int headerLength);
                position += headerLength + header.CompressedPageSize;

                if (header.Type == PageType.DictionaryPage)
                {
                    dictionaryPages++;
                    await Assert.That(header.DictionaryPageHeader).IsNotNull();
                }
                else
                {
                    await Assert.That(header.Type).IsEqualTo(expectedDataPageType);
                    values += header.DataPageHeader?.NumValues ?? header.DataPageHeaderV2!.NumValues;
                }
            }

            await Assert.That(position).IsEqualTo(end); // pages tile the chunk exactly
            await Assert.That(values).IsEqualTo(meta.NumValues);
            chunks++;
        }

        await Assert.That(chunks).IsEqualTo(2 * LeafColumns);
        await Assert.That(dictionaryPages > 0).IsTrue();
    }

    private static FileMetaData Parse(string fixture) =>
        FileMetaData.Deserialize(ReadFooterBytes(File.ReadAllBytes(TestBytes.FixturePath(fixture))));

    private static SchemaElement Find(List<SchemaElement> schema, string name) => schema.First(e => e.Name == name);

    /// <summary>Footer layout: ... footer | 4-byte little-endian footer length | "PAR1".</summary>
    private static byte[] ReadFooterBytes(byte[] file)
    {
        var tail = file.AsSpan(file.Length - 8);
        if (!tail[4..].SequenceEqual("PAR1"u8))
        {
            throw new InvalidDataException("Missing trailing PAR1 magic.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(tail);
        return file.AsSpan(file.Length - 8 - length, length).ToArray();
    }
}
