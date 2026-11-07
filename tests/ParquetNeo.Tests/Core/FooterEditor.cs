using System.Buffers;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core;

/// <summary>Test helper that changes a file's metadata by decoding the footer, editing it and writing it back.</summary>
internal static class FooterEditor
{
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
