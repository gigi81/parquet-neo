using System.Buffers;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.PageIndex;

/// <summary>The structures of the page index survive being written and read, and a damaged one is an error.</summary>
public class PageIndexMetadataTests
{
    [Test]
    public async Task AnOffsetIndexRoundTrips()
    {
        var index = new OffsetIndex();
        for (int i = 0; i < 40; i++)
        {
            index.PageLocations.Add(new PageLocation { Offset = 4 + (i * 1_000_000_000L), CompressedPageSize = 100 + i, FirstRowIndex = i * 3_000_000_000L });
        }

        var buffer = new ArrayBufferWriter<byte>();
        index.Serialize(buffer);
        var read = OffsetIndex.Deserialize(buffer.WrittenSpan);

        await Assert.That(read.PageLocations.Count).IsEqualTo(40);
        await Assert.That(read.PageLocations.Select(l => (l.Offset, l.CompressedPageSize, l.FirstRowIndex)))
            .IsEquivalentTo(index.PageLocations.Select(l => (l.Offset, l.CompressedPageSize, l.FirstRowIndex)));
    }

    [Test]
    public async Task AColumnIndexRoundTrips_WithMoreThanFifteenPages()
    {
        var index = new ColumnIndex { NullCounts = [], BoundaryOrder = 1 };
        for (int i = 0; i < 33; i++)
        {
            bool nullPage = i % 5 == 0;
            index.NullPages.Add(nullPage);
            index.MinValues.Add(nullPage ? [] : BitConverter.GetBytes((long)i));
            index.MaxValues.Add(nullPage ? [] : BitConverter.GetBytes((long)i + 10));
            index.NullCounts.Add(i);
        }

        var buffer = new ArrayBufferWriter<byte>();
        index.Serialize(buffer);
        var read = ColumnIndex.Deserialize(buffer.WrittenSpan);

        await Assert.That(read.NullPages).IsEquivalentTo(index.NullPages);
        await Assert.That(read.MinValues.Select(Convert.ToHexString)).IsEquivalentTo(index.MinValues.Select(Convert.ToHexString));
        await Assert.That(read.MaxValues.Select(Convert.ToHexString)).IsEquivalentTo(index.MaxValues.Select(Convert.ToHexString));
        await Assert.That(read.NullCounts).IsEquivalentTo(index.NullCounts);
        await Assert.That(read.BoundaryOrder).IsEqualTo(1);
    }

    [Test]
    public async Task AColumnIndexWithoutNullCounts_ReadsThemAsAbsent()
    {
        var index = new ColumnIndex();
        index.NullPages.Add(false);
        index.MinValues.Add([1]);
        index.MaxValues.Add([2]);

        var buffer = new ArrayBufferWriter<byte>();
        index.Serialize(buffer);

        await Assert.That(ColumnIndex.Deserialize(buffer.WrittenSpan).NullCounts).IsNull();
    }

    [Test]
    public async Task ATruncatedIndexIsAnError()
    {
        var index = new OffsetIndex();
        index.PageLocations.Add(new PageLocation { Offset = 4, CompressedPageSize = 10, FirstRowIndex = 0 });
        var buffer = new ArrayBufferWriter<byte>();
        index.Serialize(buffer);
        var bytes = buffer.WrittenSpan[..^3].ToArray();

        await Assert.That(() => OffsetIndex.Deserialize(bytes)).Throws<Exception>();
    }
}
