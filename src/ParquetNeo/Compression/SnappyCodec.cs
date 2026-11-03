using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ParquetNeo.Metadata;

namespace ParquetNeo.Compression;

/// <summary>
/// Snappy in its raw block format (no framing), which is what Parquet pages use. The decoder checks every
/// length and offset against the buffers; the encoder follows the reference algorithm (hash-table matching
/// within 64 KB blocks, skipping ahead faster over incompressible data).
/// </summary>
public sealed class SnappyCodec : ICodec
{
    private const int BlockSize = 1 << 16;
    private const int MaxHashTableBits = 14;

    /// <summary>The matcher reads up to this far past its position, so shorter blocks are emitted as literals.</summary>
    private const int InputMargin = 15;

    public CompressionCodec Id => CompressionCodec.Snappy;

    public int GetMaxCompressedLength(int uncompressedLength)
    {
        long bound = 32L + uncompressedLength + uncompressedLength / 6;
        return bound > int.MaxValue ? throw new ArgumentOutOfRangeException(nameof(uncompressedLength)) : (int)bound;
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        int written = WriteVarint(destination, (uint)source.Length);
        Span<ushort> table = stackalloc ushort[1 << MaxHashTableBits];
        for (int offset = 0; offset < source.Length; offset += BlockSize)
        {
            var block = source.Slice(offset, Math.Min(BlockSize, source.Length - offset));
            written = CompressBlock(block, destination, written, table);
        }

        return written;
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty && destination.IsEmpty)
        {
            return; // tolerated: some writers emit nothing for an empty page
        }

        int ip = 0;
        uint expectedLength = ReadPreamble(source, ref ip);
        if (expectedLength != (uint)destination.Length)
        {
            throw Corrupt($"it declares {expectedLength} uncompressed bytes but the page header says {destination.Length}");
        }

        int op = 0;
        while (ip < source.Length)
        {
            int tag = source[ip++];
            int length;
            int offset;
            switch (tag & 3)
            {
                case 0: // literal
                    length = tag >> 2;
                    if (length >= 60)
                    {
                        int extraBytes = length - 59; // the length-1 follows in 1 to 4 bytes
                        if (extraBytes > source.Length - ip)
                        {
                            throw Corrupt("a literal length is cut off");
                        }

                        uint value = 0;
                        for (int i = 0; i < extraBytes; i++)
                        {
                            value |= (uint)source[ip + i] << (8 * i);
                        }

                        ip += extraBytes;
                        if (value >= int.MaxValue)
                        {
                            throw Corrupt("a literal is impossibly long");
                        }

                        length = (int)value;
                    }

                    length++;
                    if (length > source.Length - ip || length > destination.Length - op)
                    {
                        throw Corrupt("a literal runs past the end of the data");
                    }

                    source.Slice(ip, length).CopyTo(destination.Slice(op));
                    ip += length;
                    op += length;
                    continue;

                case 1: // copy with a 1-byte offset (and 3 high bits in the tag)
                    if (ip >= source.Length)
                    {
                        throw Corrupt("a copy is cut off");
                    }

                    length = 4 + ((tag >> 2) & 7);
                    offset = ((tag >> 5) << 8) | source[ip++];
                    break;

                case 2: // copy with a 2-byte offset
                    if (source.Length - ip < 2)
                    {
                        throw Corrupt("a copy is cut off");
                    }

                    length = 1 + (tag >> 2);
                    offset = source[ip] | (source[ip + 1] << 8);
                    ip += 2;
                    break;

                default: // copy with a 4-byte offset
                    if (source.Length - ip < 4)
                    {
                        throw Corrupt("a copy is cut off");
                    }

                    length = 1 + (tag >> 2);
                    uint wideOffset = (uint)(source[ip] | (source[ip + 1] << 8) | (source[ip + 2] << 16)) | ((uint)source[ip + 3] << 24);
                    if (wideOffset > int.MaxValue)
                    {
                        throw Corrupt("a copy offset is impossibly large");
                    }

                    offset = (int)wideOffset;
                    ip += 4;
                    break;
            }

            if (offset == 0 || offset > op)
            {
                throw Corrupt("a copy reaches before the start of the output");
            }

            if (length > destination.Length - op)
            {
                throw Corrupt("a copy runs past the declared size");
            }

            CopyMatch(destination, op, offset, length);
            op += length;
        }

        if (op != destination.Length)
        {
            throw Corrupt($"it decodes to {op} bytes but declares {destination.Length}");
        }
    }

    /// <summary>Copies <paramref name="length"/> bytes from <paramref name="offset"/> bytes back; the ranges may overlap.</summary>
    private static void CopyMatch(Span<byte> destination, int op, int offset, int length)
    {
        var region = destination.Slice(op - offset); // starts at the source of the copy
        if (offset >= length)
        {
            region.Slice(0, length).CopyTo(region.Slice(offset));
            return;
        }

        // Overlapping copy: the output repeats the last `offset` bytes. Copy the growing prefix in chunks,
        // which doubles the copied size each time while keeping the pattern aligned.
        int copied = offset;
        int remaining = length;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, copied);
            region.Slice(0, chunk).CopyTo(region.Slice(copied));
            copied += chunk;
            remaining -= chunk;
        }
    }

    private static uint ReadPreamble(ReadOnlySpan<byte> source, ref int ip)
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (ip >= source.Length)
            {
                throw Corrupt("the length preamble is cut off");
            }

            byte b = source[ip++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Corrupt("the length preamble is longer than 5 bytes");
    }

    private static ParquetFormatException Corrupt(string detail) => new($"Snappy data is corrupt: {detail}.");

    private static int CompressBlock(ReadOnlySpan<byte> input, Span<byte> output, int op, Span<ushort> table)
    {
        int nextEmit = 0;
        if (input.Length >= InputMargin)
        {
            int tableSize = 256;
            int tableBits = 8;
            while (tableSize < (1 << MaxHashTableBits) && tableSize < input.Length)
            {
                tableSize <<= 1;
                tableBits++;
            }

            int shift = 32 - tableBits;
            table = table.Slice(0, tableSize);
            table.Clear();

            ref byte start = ref MemoryMarshal.GetReference(input);
            int ipLimit = input.Length - InputMargin;
            int ip = 1;
            uint nextHash = Hash(Load32(ref start, ip), shift);

            while (true)
            {
                // Look for a 4-byte match, probing further apart the longer nothing matches.
                uint skip = 32;
                int nextIp = ip;
                int candidate;
                do
                {
                    ip = nextIp;
                    uint hash = nextHash;
                    nextIp = ip + (int)(skip++ >> 5);
                    if (nextIp > ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    nextHash = Hash(Load32(ref start, nextIp), shift);
                    candidate = table[(int)hash];
                    table[(int)hash] = (ushort)ip;
                }
                while (Load32(ref start, ip) != Load32(ref start, candidate));

                op = EmitLiteral(output, op, input.Slice(nextEmit, ip - nextEmit));

                // Emit the match, then keep matching from where it ended while the next 4 bytes also match.
                do
                {
                    int matched = 4 + input.Slice(candidate + 4).CommonPrefixLength(input.Slice(ip + 4));
                    int offset = ip - candidate;
                    ip += matched;
                    op = EmitCopy(output, op, offset, matched);
                    nextEmit = ip;
                    if (ip >= ipLimit)
                    {
                        goto EmitRemainder;
                    }

                    table[(int)Hash(Load32(ref start, ip - 1), shift)] = (ushort)(ip - 1);
                    uint currentHash = Hash(Load32(ref start, ip), shift);
                    candidate = table[(int)currentHash];
                    table[(int)currentHash] = (ushort)ip;
                }
                while (Load32(ref start, ip) == Load32(ref start, candidate));

                nextHash = Hash(Load32(ref start, ++ip), shift);
            }
        }

    EmitRemainder:
        if (nextEmit < input.Length)
        {
            op = EmitLiteral(output, op, input.Slice(nextEmit));
        }

        return op;
    }

    private static int EmitLiteral(Span<byte> output, int op, ReadOnlySpan<byte> literal)
    {
        if (literal.IsEmpty)
        {
            return op;
        }

        int n = literal.Length - 1;
        if (n < 60)
        {
            output[op++] = (byte)(n << 2);
        }
        else
        {
            int extraBytes = n < (1 << 8) ? 1 : n < (1 << 16) ? 2 : n < (1 << 24) ? 3 : 4;
            output[op++] = (byte)((59 + extraBytes) << 2);
            for (int i = 0; i < extraBytes; i++)
            {
                output[op++] = (byte)(n >> (8 * i));
            }
        }

        literal.CopyTo(output.Slice(op));
        return op + literal.Length;
    }

    private static int EmitCopy(Span<byte> output, int op, int offset, int length)
    {
        // Split so that every piece, including the last, is at least 4 bytes (the minimum a copy can encode).
        while (length >= 68)
        {
            op = EmitCopyAtMost64(output, op, offset, 64);
            length -= 64;
        }

        if (length > 64)
        {
            op = EmitCopyAtMost64(output, op, offset, 60);
            length -= 60;
        }

        return EmitCopyAtMost64(output, op, offset, length);
    }

    private static int EmitCopyAtMost64(Span<byte> output, int op, int offset, int length)
    {
        if (length < 12 && offset < 2048)
        {
            output[op++] = (byte)(1 | ((length - 4) << 2) | ((offset >> 8) << 5));
            output[op++] = (byte)offset;
        }
        else
        {
            output[op++] = (byte)(2 | ((length - 1) << 2));
            output[op++] = (byte)offset;
            output[op++] = (byte)(offset >> 8);
        }

        return op;
    }

    private static int WriteVarint(Span<byte> destination, uint value)
    {
        int length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Load32(ref byte start, int index) => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, index));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(uint bytes, int shift) => (bytes * 0x1E35A7BD) >> shift;
}
