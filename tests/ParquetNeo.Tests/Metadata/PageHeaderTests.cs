using ParquetNeo.Metadata;
using ParquetNeo.Thrift;

namespace ParquetNeo.Tests.Metadata;

public class PageHeaderTests
{
    [Test]
    public async Task DataPageV1Header_SurvivesRoundTrip()
    {
        var header = new PageHeader
        {
            Type = PageType.DataPage,
            UncompressedPageSize = 1000,
            CompressedPageSize = 600,
            Crc = -12345,
            DataPageHeader = new DataPageHeader
            {
                NumValues = 250,
                Encoding = ParquetEncoding.RleDictionary,
                DefinitionLevelEncoding = ParquetEncoding.Rle,
                RepetitionLevelEncoding = ParquetEncoding.Rle,
                Statistics = SampleMetadata.Statistics(),
            },
        };

        var parsed = PageHeader.Deserialize(header.ToBytes(), out int consumed);

        await Assert.That(consumed).IsEqualTo(header.ToBytes().Length);
        await Assert.That(parsed.Type).IsEqualTo(PageType.DataPage);
        await Assert.That(parsed.UncompressedPageSize).IsEqualTo(1000);
        await Assert.That(parsed.CompressedPageSize).IsEqualTo(600);
        await Assert.That(parsed.Crc).IsEqualTo(-12345);
        await Assert.That(parsed.DataPageHeader!.NumValues).IsEqualTo(250);
        await Assert.That(parsed.DataPageHeader.Encoding).IsEqualTo(ParquetEncoding.RleDictionary);
        await Assert.That(parsed.DataPageHeader.DefinitionLevelEncoding).IsEqualTo(ParquetEncoding.Rle);
        await Assert.That(parsed.DataPageHeader.RepetitionLevelEncoding).IsEqualTo(ParquetEncoding.Rle);
        await Assert.That(parsed.DataPageHeader.Statistics!.NullCount).IsEqualTo(2L);
        await Assert.That(parsed.DictionaryPageHeader).IsNull();
        await Assert.That(parsed.DataPageHeaderV2).IsNull();
    }

    [Test]
    public async Task DataPageV2Header_SurvivesRoundTrip()
    {
        var header = new PageHeader
        {
            Type = PageType.DataPageV2,
            UncompressedPageSize = 80,
            CompressedPageSize = 40,
            DataPageHeaderV2 = new DataPageHeaderV2
            {
                NumValues = 10,
                NumNulls = 2,
                NumRows = 8,
                Encoding = ParquetEncoding.DeltaBinaryPacked,
                DefinitionLevelsByteLength = 3,
                RepetitionLevelsByteLength = 4,
                IsCompressed = false,
                Statistics = SampleMetadata.Statistics(),
            },
        };

        var parsed = PageHeader.Deserialize(header.ToBytes(), out _);
        var v2 = parsed.DataPageHeaderV2!;

        await Assert.That(parsed.Crc).IsNull();
        await Assert.That(v2.NumValues).IsEqualTo(10);
        await Assert.That(v2.NumNulls).IsEqualTo(2);
        await Assert.That(v2.NumRows).IsEqualTo(8);
        await Assert.That(v2.Encoding).IsEqualTo(ParquetEncoding.DeltaBinaryPacked);
        await Assert.That(v2.DefinitionLevelsByteLength).IsEqualTo(3);
        await Assert.That(v2.RepetitionLevelsByteLength).IsEqualTo(4);
        await Assert.That(v2.IsCompressed).IsFalse();
        await Assert.That(v2.Statistics).IsNotNull();
    }

    [Test]
    public async Task DataPageV2_IsCompressedDefaultsToTrueWhenAbsent()
    {
        var bytes = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(1, (int)PageType.DataPageV2);
            w.WriteI32Field(2, 10);
            w.WriteI32Field(3, 10);
            w.BeginStructField(8);
            w.WriteI32Field(1, 5); // num_values only; is_compressed omitted
            w.EndStruct();
            w.EndStruct();
        });

        var parsed = PageHeader.Deserialize(bytes, out _);

        await Assert.That(parsed.DataPageHeaderV2!.IsCompressed).IsTrue();
    }

    [Test]
    public async Task DictionaryPageHeader_SurvivesRoundTrip()
    {
        var header = new PageHeader
        {
            Type = PageType.DictionaryPage,
            UncompressedPageSize = 16,
            CompressedPageSize = 16,
            DictionaryPageHeader = new DictionaryPageHeader
            {
                NumValues = 4,
                Encoding = ParquetEncoding.Plain,
                IsSorted = true,
            },
        };

        var parsed = PageHeader.Deserialize(header.ToBytes(), out _);

        await Assert.That(parsed.DictionaryPageHeader!.NumValues).IsEqualTo(4);
        await Assert.That(parsed.DictionaryPageHeader.Encoding).IsEqualTo(ParquetEncoding.Plain);
        await Assert.That(parsed.DictionaryPageHeader.IsSorted).IsTrue();
    }

    [Test]
    public async Task Deserialize_ReportsHeaderLengthSoPageDataCanFollow()
    {
        var header = new PageHeader
        {
            Type = PageType.DictionaryPage,
            DictionaryPageHeader = new DictionaryPageHeader { NumValues = 1 },
        };
        var headerBytes = header.ToBytes();
        var withPageData = headerBytes.Concat(Enumerable.Repeat((byte)0xFF, 64)).ToArray();

        PageHeader.Deserialize(withPageData, out int consumed);

        await Assert.That(consumed).IsEqualTo(headerBytes.Length);
    }

    [Test]
    public async Task EmptyIndexPageHeader_IsSkipped()
    {
        var bytes = TestBytes.Write((ref ThriftCompactWriter w) =>
        {
            w.BeginStruct();
            w.WriteI32Field(1, (int)PageType.IndexPage);
            w.WriteI32Field(2, 0);
            w.WriteI32Field(3, 0);
            w.BeginStructField(6);
            w.EndStruct();
            w.EndStruct();
        });

        var parsed = PageHeader.Deserialize(bytes, out int consumed);

        await Assert.That(parsed.Type).IsEqualTo(PageType.IndexPage);
        await Assert.That(consumed).IsEqualTo(bytes.Length);
    }
}
