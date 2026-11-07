using System.Buffers;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core;

/// <summary>Test helper that changes a file's metadata by decoding the footer, editing it and writing it back.</summary>
internal static class FooterEditor
{
    /// <summary>
    /// Replaces the header of page <paramref name="pageIndex"/> of the only column chunk, which may change its length;
    /// the chunk's sizes and the footer are updated to match.
    /// </summary>
    public static byte[] PatchPageHeader(byte[] original, int pageIndex, Action<PageHeader> change)
    {
        var file = TestParquetFile.Parse(original);
        var page = file.Pages(0, 0)[pageIndex];
        var header = PageHeader.Deserialize(original.AsSpan((int)page.Offset), out _);
        change(header);
        var buffer = new ArrayBufferWriter<byte>();
        header.Serialize(buffer);
        int delta = buffer.WrittenCount - page.HeaderLength;

        var body = new List<byte>(original.AsSpan(0, (int)page.Offset).ToArray());
        body.AddRange(buffer.WrittenSpan.ToArray());
        body.AddRange(original.AsSpan((int)page.Offset + page.HeaderLength, (int)file.FooterStart - (int)page.Offset - page.HeaderLength).ToArray());
        bool dictionaryFirst = file.Chunk(0, 0).DictionaryPageOffset is not null;

        return Rewrite(body.Concat(original[(int)file.FooterStart..]).ToArray(), f =>
        {
            var group = f.RowGroups[0];
            var chunk = group.Columns[0].MetaData!;
            chunk.TotalCompressedSize += delta;
            chunk.TotalUncompressedSize += delta;
            group.TotalByteSize += delta;
            group.TotalCompressedSize += delta;
            if (dictionaryFirst && pageIndex == 0)
            {
                chunk.DataPageOffset += delta;
            }
        }, body.Count);
    }

    /// <param name="footerStart">Where the new footer starts, when the data before it was changed in length.</param>
    public static byte[] Rewrite(byte[] bytes, Action<FileMetaData> change, int? footerStart = null)
    {
        var file = TestParquetFile.Parse(bytes);
        change(file.Metadata);
        var footer = new ArrayBufferWriter<byte>();
        file.Metadata.Serialize(footer);

        var result = new List<byte>(bytes.AsSpan(0, footerStart ?? (int)file.FooterStart).ToArray());
        result.AddRange(footer.WrittenSpan.ToArray());
        result.AddRange(BitConverter.GetBytes(footer.WrittenCount));
        result.AddRange("PAR1"u8.ToArray());
        return result.ToArray();
    }
}
