using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Thrift;

namespace ParquetNeo.Tests.Metadata;

public class FileMetaDataTests
{
    [Test]
    public async Task FullyPopulatedMetadata_SurvivesRoundTrip()
    {
        var original = SampleMetadata.FileMetaData();
        var bytes = original.ToBytes();

        var parsed = FileMetaData.Deserialize(bytes);

        // Serializing the parsed copy must reproduce the exact bytes: nothing was lost or reordered.
        await Assert.That(TestBytes.ToHex(parsed.ToBytes())).IsEqualTo(TestBytes.ToHex(bytes));
        await Assert.That(parsed.Version).IsEqualTo(2);
        await Assert.That(parsed.NumRows).IsEqualTo(200L);
        await Assert.That(parsed.CreatedBy).IsEqualTo("ParquetNeo tests");
        await Assert.That(parsed.Schema.Count).IsEqualTo(original.Schema.Count);
        await Assert.That(parsed.RowGroups.Count).IsEqualTo(2);
        await Assert.That(parsed.KeyValueMetadata![0].Key).IsEqualTo("writer");
        await Assert.That(parsed.ColumnOrders).IsEquivalentTo(original.ColumnOrders!);
    }

    [Test]
    public async Task ColumnMetaData_PreservesAllFields()
    {
        var parsed = FileMetaData.Deserialize(SampleMetadata.FileMetaData().ToBytes());
        var column = parsed.RowGroups[1].Columns[0];
        var meta = column.MetaData!;

        await Assert.That(column.FilePath).IsEqualTo("other.parquet");
        await Assert.That(column.FileOffset).IsEqualTo(77L);
        await Assert.That(column.OffsetIndexOffset).IsEqualTo(1L);
        await Assert.That(column.OffsetIndexLength).IsEqualTo(2);
        await Assert.That(column.ColumnIndexOffset).IsEqualTo(3L);
        await Assert.That(column.ColumnIndexLength).IsEqualTo(4);
        await Assert.That(meta.Type).IsEqualTo(PhysicalType.Int64);
        await Assert.That(meta.Encodings).IsEquivalentTo(
            new[] { ParquetEncoding.Plain, ParquetEncoding.Rle, ParquetEncoding.RleDictionary });
        await Assert.That(meta.PathInSchema).IsEquivalentTo(new[] { "parent", "child" });
        await Assert.That(meta.Codec).IsEqualTo(CompressionCodec.Zstd);
        await Assert.That(meta.NumValues).IsEqualTo(1_000_000_000_000L);
        await Assert.That(meta.TotalUncompressedSize).IsEqualTo(2_000_000_000_000L);
        await Assert.That(meta.TotalCompressedSize).IsEqualTo(1_000_000_000L);
        await Assert.That(meta.DataPageOffset).IsEqualTo(4096L);
        await Assert.That(meta.IndexPageOffset).IsEqualTo(5000L);
        await Assert.That(meta.DictionaryPageOffset).IsEqualTo(4L);
        await Assert.That(meta.KeyValueMetadata![1].Value).IsNull();
        await Assert.That(meta.EncodingStats![1].Count).IsEqualTo(20);
        await Assert.That(meta.BloomFilterOffset).IsEqualTo(123_456L);
        await Assert.That(meta.BloomFilterLength).IsEqualTo(1024);
        await Assert.That(meta.SizeStatistics!.UnencodedByteArrayDataBytes).IsEqualTo(99L);
        await Assert.That(meta.SizeStatistics.RepetitionLevelHistogram).IsEquivalentTo(new long[] { 1, 2, 3 });
        await Assert.That(meta.SizeStatistics.DefinitionLevelHistogram).IsEquivalentTo(new long[] { 4, 5 });
    }

    [Test]
    public async Task Statistics_PreservesAllFields()
    {
        var parsed = FileMetaData.Deserialize(SampleMetadata.FileMetaData().ToBytes());
        var stats = parsed.RowGroups[0].Columns[0].MetaData!.Statistics!;

        await Assert.That(TestBytes.ToHex(stats.Max!)).IsEqualTo("09");
        await Assert.That(TestBytes.ToHex(stats.Min!)).IsEqualTo("01");
        await Assert.That(TestBytes.ToHex(stats.MaxValue!)).IsEqualTo("0900");
        await Assert.That(TestBytes.ToHex(stats.MinValue!)).IsEqualTo("0100");
        await Assert.That(stats.NullCount).IsEqualTo(2L);
        await Assert.That(stats.DistinctCount).IsEqualTo(7L);
        await Assert.That(stats.NanCount).IsEqualTo(3L);
        await Assert.That(stats.IsMaxValueExact).IsTrue();
        await Assert.That(stats.IsMinValueExact).IsFalse();
    }

    [Test]
    public async Task RowGroup_PreservesAllFields()
    {
        var parsed = FileMetaData.Deserialize(SampleMetadata.FileMetaData().ToBytes());
        var group = parsed.RowGroups[0];

        await Assert.That(group.Columns.Count).IsEqualTo(2);
        await Assert.That(group.TotalByteSize).IsEqualTo(1234L);
        await Assert.That(group.NumRows).IsEqualTo(100L);
        await Assert.That(group.FileOffset).IsEqualTo(4L);
        await Assert.That(group.TotalCompressedSize).IsEqualTo(999L);
        await Assert.That(group.Ordinal).IsEqualTo((short)3);
        await Assert.That(group.SortingColumns![0].ColumnIndex).IsEqualTo(1);
        await Assert.That(group.SortingColumns[0].Descending).IsTrue();
        await Assert.That(group.SortingColumns[0].NullsFirst).IsFalse();
    }

    [Test]
    public async Task SchemaElement_PreservesAllFields()
    {
        var parsed = FileMetaData.Deserialize(SampleMetadata.FileMetaData().ToBytes());
        var root = parsed.Schema[0];
        var legacy = parsed.Schema[1];

        await Assert.That(root.Name).IsEqualTo("schema");
        await Assert.That(root.NumChildren).IsEqualTo(SampleMetadata.AllLogicalTypes().Count + 1);
        await Assert.That(root.Type).IsNull();
        await Assert.That(root.RepetitionType).IsNull();
        await Assert.That(legacy.Type).IsEqualTo(PhysicalType.FixedLenByteArray);
        await Assert.That(legacy.TypeLength).IsEqualTo(16);
        await Assert.That(legacy.RepetitionType).IsEqualTo(FieldRepetitionType.Optional);
        await Assert.That(legacy.ConvertedType).IsEqualTo(ConvertedType.Decimal);
        await Assert.That(legacy.Scale).IsEqualTo(3);
        await Assert.That(legacy.Precision).IsEqualTo(20);
        await Assert.That(legacy.FieldId).IsEqualTo(42);
        await Assert.That(legacy.LogicalType).IsNull();
    }

    [Test]
    public async Task EveryLogicalType_SurvivesRoundTrip()
    {
        var parsed = FileMetaData.Deserialize(SampleMetadata.FileMetaData().ToBytes());
        var expected = SampleMetadata.AllLogicalTypes();

        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(parsed.Schema[i + 2].LogicalType).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task MinimalMetadata_ParsesWithOptionalFieldsAbsent()
    {
        var minimal = new FileMetaData
        {
            Version = 1,
            Schema = [new SchemaElement { Name = "root" }],
            NumRows = 0,
        };

        var parsed = FileMetaData.Deserialize(minimal.ToBytes());

        await Assert.That(parsed.CreatedBy).IsNull();
        await Assert.That(parsed.KeyValueMetadata).IsNull();
        await Assert.That(parsed.ColumnOrders).IsNull();
        await Assert.That(parsed.RowGroups.Count).IsEqualTo(0);
        await Assert.That(parsed.Schema[0].Name).IsEqualTo("root");
    }

    [Test]
    public async Task UnknownFields_AreSkippedAtAnyLevel()
    {
        // version=2, schema=[], num_rows=7, row_groups=[], plus fields this library does not model.
        var bytes = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(1, 2);
            w.BeginListField(2, CompactType.Struct, 1);
            w.BeginStruct();
            w.WriteStringField(4, "root");
            w.WriteBinaryField(50, [1, 2, 3]); // unknown field inside a SchemaElement
            w.EndStruct();
            w.WriteI64Field(3, 7);
            w.BeginListField(4, CompactType.Struct, 0);
            w.BeginStructField(8); // encryption_algorithm: not modelled yet
            w.WriteBoolField(1, true);
            w.EndStruct();
            w.WriteBinaryField(9, [9, 9]); // footer_signing_key_metadata
            w.BeginListField(100, CompactType.I32, 2);
            w.WriteI32(1);
            w.WriteI32(2);
            w.EndStruct();
        });

        var parsed = FileMetaData.Deserialize(bytes);

        await Assert.That(parsed.Version).IsEqualTo(2);
        await Assert.That(parsed.NumRows).IsEqualTo(7L);
        await Assert.That(parsed.Schema[0].Name).IsEqualTo("root");
    }

    [Test]
    public async Task FieldWithUnexpectedWireType_IsSkippedInsteadOfMisread()
    {
        // num_rows (id 3) written as a binary instead of i64.
        var bytes = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(1, 2);
            w.WriteStringField(3, "not a number");
            w.EndStruct();
        });

        var parsed = FileMetaData.Deserialize(bytes);

        await Assert.That(parsed.Version).IsEqualTo(2);
        await Assert.That(parsed.NumRows).IsEqualTo(0L);
    }

    [Test]
    public async Task ExtensionField32767_FromTheSpecExample_IsIgnored()
    {
        // Replace the stop byte with: 08 FF FF 01 (binary field 32767), ULEB128(size), payload, 00.
        var original = SampleMetadata.FileMetaData();
        var plain = original.ToBytes();
        var extension = TestBytes.Hex("08 FF FF 01 05 AA BB CC DD EE 00");
        var withExtension = plain[..^1].Concat(extension).ToArray();

        var parsed = FileMetaData.Deserialize(withExtension);

        await Assert.That(TestBytes.ToHex(parsed.ToBytes())).IsEqualTo(TestBytes.ToHex(plain));
    }

    [Test]
    public async Task BytesAfterTheStruct_AreIgnored()
    {
        var plain = SampleMetadata.FileMetaData().ToBytes();
        var withTrailer = plain.Concat(TestBytes.Hex("DEADBEEF")).ToArray();

        var parsed = FileMetaData.Deserialize(withTrailer);

        await Assert.That(parsed.NumRows).IsEqualTo(200L);
    }

    [Test]
    public async Task TruncatedFooter_ThrowsMetadataException()
    {
        var bytes = SampleMetadata.FileMetaData().ToBytes();

        await Assert.That(() => FileMetaData.Deserialize(bytes.AsSpan(0, bytes.Length / 2).ToArray()))
            .Throws<MetadataException>();
    }
}
