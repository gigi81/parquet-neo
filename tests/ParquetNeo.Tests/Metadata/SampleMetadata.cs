using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Metadata;

/// <summary>Builders for metadata objects with every optional field populated.</summary>
internal static class SampleMetadata
{
    public static Statistics Statistics() => new()
    {
        Max = [9],
        Min = [1],
        NullCount = 2,
        DistinctCount = 7,
        MaxValue = [9, 0],
        MinValue = [1, 0],
        IsMaxValueExact = true,
        IsMinValueExact = false,
        NanCount = 3,
    };

    public static ColumnMetaData ColumnMetaData() => new()
    {
        Type = PhysicalType.Int64,
        Encodings = [ParquetEncoding.Plain, ParquetEncoding.Rle, ParquetEncoding.RleDictionary],
        PathInSchema = ["parent", "child"],
        Codec = CompressionCodec.Zstd,
        NumValues = 1_000_000_000_000,
        TotalUncompressedSize = 2_000_000_000_000,
        TotalCompressedSize = 1_000_000_000,
        KeyValueMetadata = [new KeyValue { Key = "k", Value = "v" }, new KeyValue { Key = "no-value" }],
        DataPageOffset = 4096,
        IndexPageOffset = 5000,
        DictionaryPageOffset = 4,
        Statistics = Statistics(),
        EncodingStats =
        [
            new PageEncodingStats { PageType = PageType.DictionaryPage, Encoding = ParquetEncoding.Plain, Count = 1 },
            new PageEncodingStats { PageType = PageType.DataPage, Encoding = ParquetEncoding.RleDictionary, Count = 20 },
        ],
        BloomFilterOffset = 123_456,
        BloomFilterLength = 1024,
        SizeStatistics = new SizeStatistics
        {
            UnencodedByteArrayDataBytes = 99,
            RepetitionLevelHistogram = [1, 2, 3],
            DefinitionLevelHistogram = [4, 5],
        },
    };

    public static ColumnChunk ColumnChunk() => new()
    {
        FilePath = "other.parquet",
        FileOffset = 77,
        MetaData = ColumnMetaData(),
        OffsetIndexOffset = 1,
        OffsetIndexLength = 2,
        ColumnIndexOffset = 3,
        ColumnIndexLength = 4,
    };

    public static RowGroup RowGroup() => new()
    {
        Columns = [ColumnChunk(), ColumnChunk()],
        TotalByteSize = 1234,
        NumRows = 100,
        SortingColumns = [new SortingColumn { ColumnIndex = 1, Descending = true, NullsFirst = false }],
        FileOffset = 4,
        TotalCompressedSize = 999,
        Ordinal = 3,
    };

    public static IReadOnlyList<LogicalType> AllLogicalTypes() =>
    [
        LogicalType.String,
        LogicalType.Map,
        LogicalType.List,
        LogicalType.Enum,
        LogicalType.Date,
        LogicalType.Null,
        LogicalType.Json,
        LogicalType.Bson,
        LogicalType.Uuid,
        LogicalType.Float16,
        LogicalType.Decimal(scale: 2, precision: 10),
        LogicalType.Time(isAdjustedToUtc: true, TimeUnit.Millis),
        LogicalType.Time(isAdjustedToUtc: false, TimeUnit.Nanos),
        LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Micros),
        LogicalType.Timestamp(isAdjustedToUtc: false, TimeUnit.Millis),
        LogicalType.Integer(bitWidth: 8, isSigned: true),
        LogicalType.Integer(bitWidth: 64, isSigned: false),
    ];

    public static FileMetaData FileMetaData()
    {
        var schema = new List<SchemaElement>
        {
            new() { Name = "schema", NumChildren = AllLogicalTypes().Count + 1 },
            new()
            {
                Name = "legacy",
                Type = PhysicalType.FixedLenByteArray,
                TypeLength = 16,
                RepetitionType = FieldRepetitionType.Optional,
                ConvertedType = ConvertedType.Decimal,
                Scale = 3,
                Precision = 20,
                FieldId = 42,
            },
        };
        int index = 0;
        foreach (var logicalType in AllLogicalTypes())
        {
            schema.Add(new SchemaElement
            {
                Name = $"column_{index++}",
                Type = PhysicalType.ByteArray,
                RepetitionType = FieldRepetitionType.Repeated,
                LogicalType = logicalType,
            });
        }

        return new FileMetaData
        {
            Version = 2,
            Schema = schema,
            NumRows = 200,
            RowGroups = [RowGroup(), RowGroup()],
            KeyValueMetadata = [new KeyValue { Key = "writer", Value = "tests" }],
            CreatedBy = "ParquetNeo tests",
            ColumnOrders = [ColumnOrder.TypeDefined, ColumnOrder.Ieee754TotalOrder, ColumnOrder.Int96Timestamp],
        };
    }
}
