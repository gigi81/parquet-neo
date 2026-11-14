using System.Buffers.Binary;

namespace ParquetNeo.Encodings;

/// <summary>The CRC-32 of the standard (the polynomial 0x04C11DB7, reflected as 0xEDB88320, which gzip and zip use), as Parquet page checksums are.</summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    /// <summary>The tables of the slicing-by-8 method: table 0 is the usual one byte at a time, and table k is a byte followed by k zero bytes.</summary>
    private static readonly uint[] Tables = CreateTables();

    /// <summary>The checksum of the bytes.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>The checksum of the bytes that came before, followed by these: <c>Append(Compute(a), b)</c> is the checksum of <c>a</c> and <c>b</c> one after the other.</summary>
    public static uint Append(uint previous, ReadOnlySpan<byte> data)
    {
        uint crc = ~previous;
        var tables = Tables;
        while (data.Length >= 8)
        {
            uint low = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = tables[(7 * 256) + (low & 0xFF)]
                ^ tables[(6 * 256) + ((low >> 8) & 0xFF)]
                ^ tables[(5 * 256) + ((low >> 16) & 0xFF)]
                ^ tables[(4 * 256) + (low >> 24)]
                ^ tables[(3 * 256) + (high & 0xFF)]
                ^ tables[(2 * 256) + ((high >> 8) & 0xFF)]
                ^ tables[256 + ((high >> 16) & 0xFF)]
                ^ tables[high >> 24];
            data = data[8..];
        }

        foreach (byte value in data)
        {
            crc = tables[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTables()
    {
        var tables = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            tables[i] = crc;
        }

        for (int table = 1; table < 8; table++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint previous = tables[((table - 1) * 256) + i];
                tables[(table * 256) + i] = (previous >> 8) ^ tables[previous & 0xFF];
            }
        }

        return tables;
    }
}
