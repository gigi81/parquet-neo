using System.Buffers.Binary;
using System.Numerics;

namespace ParquetNeo.Encodings;

/// <summary>The 64-bit xxHash (XXH64) with seed 0, the hash Parquet bloom filters use.</summary>
internal static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        int length = data.Length;
        ulong hash;
        if (length >= 32)
        {
            ulong v1 = unchecked(Prime1 + Prime2);
            ulong v2 = Prime2;
            ulong v3 = 0;
            ulong v4 = unchecked(0 - Prime1);
            while (data.Length >= 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[8..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[16..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[24..]));
                data = data[32..];
            }

            hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            hash = Merge(hash, v1);
            hash = Merge(hash, v2);
            hash = Merge(hash, v3);
            hash = Merge(hash, v4);
        }
        else
        {
            hash = Prime5;
        }

        unchecked
        {
            hash += (ulong)length;
            while (data.Length >= 8)
            {
                hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data));
                hash = (BitOperations.RotateLeft(hash, 27) * Prime1) + Prime4;
                data = data[8..];
            }

            if (data.Length >= 4)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data) * Prime1;
                hash = (BitOperations.RotateLeft(hash, 23) * Prime2) + Prime3;
                data = data[4..];
            }

            foreach (byte value in data)
            {
                hash ^= value * Prime5;
                hash = BitOperations.RotateLeft(hash, 11) * Prime1;
            }

            hash ^= hash >> 33;
            hash *= Prime2;
            hash ^= hash >> 29;
            hash *= Prime3;
            hash ^= hash >> 32;
        }

        return hash;
    }

    private static ulong Round(ulong accumulator, ulong input) => unchecked(BitOperations.RotateLeft(accumulator + (input * Prime2), 31) * Prime1);

    private static ulong Merge(ulong hash, ulong value) => unchecked(((hash ^ Round(0, value)) * Prime1) + Prime4);
}
