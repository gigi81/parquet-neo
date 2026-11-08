using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Encodings;

/// <summary>
/// Streaming decoder for the RLE/bit-packing hybrid encoding (definition and repetition levels,
/// dictionary indices, boolean values). It works on the encoded data only: a page's 4-byte length
/// prefix, or a dictionary index page's bit-width byte, is handled by the caller.
/// </summary>
/// <remarks>
/// Values are returned as <see cref="int"/>. For a bit width of 32 a value above <see cref="int.MaxValue"/>
/// shows up as a negative number; callers that index with the result should treat it as unsigned.
/// A run may extend past the number of values the caller needs, so the caller bounds the reading.
/// </remarks>
internal ref struct RleBitPackedDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _bitWidth;
    private int _position;

    private long _runRemaining; // values of the current run not yet returned (including buffered ones)
    private bool _isRepeated;
    private uint _repeatedValue;
    private int _packedPosition; // byte offset of the next unread group of the current bit-packed run

    public DecodedGroup Group;
    private int _groupIndex;
    private int _groupCount;

    public RleBitPackedDecoder(ReadOnlySpan<byte> data, int bitWidth)
    {
        if ((uint)bitWidth > BitPacking.MaxBitWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "The bit width must be between 0 and 32.");
        }

        _data = data;
        _bitWidth = bitWidth;
    }

    /// <summary>Decodes up to <paramref name="destination"/>.Length values; returns fewer only when the data ends.</summary>
    /// <exception cref="EncodingException">A run header or repeated value is truncated or malformed.</exception>
    public int ReadValues(scoped Span<int> destination)
    {
        var output = MemoryMarshal.Cast<int, uint>(destination);
        int written = 0;
        while (written < output.Length)
        {
            if (_runRemaining == 0)
            {
                if (!NextRun())
                {
                    break;
                }

                continue; // a run of length zero is skipped
            }

            int count = (int)Math.Min(output.Length - written, _runRemaining);
            var chunk = output.Slice(written, count);
            if (_isRepeated)
            {
                chunk.Fill(_repeatedValue);
            }
            else
            {
                ReadPacked(chunk);
            }

            written += count;
            _runRemaining -= count;
        }

        return written;
    }

    private bool NextRun()
    {
        if (_position >= _data.Length)
        {
            return false;
        }

        uint header = ReadVarint32();
        if ((header & 1) == 1)
        {
            long groups = header >> 1;
            if (_bitWidth > 0)
            {
                // Never trust the declared length beyond the bytes that are actually there.
                groups = Math.Min(groups, (_data.Length - _position) / _bitWidth);
            }

            _isRepeated = false;
            _packedPosition = _position;
            _position += (int)(groups * _bitWidth);
            _runRemaining = groups * 8;
            _groupIndex = _groupCount = 0;
        }
        else
        {
            int valueBytes = (_bitWidth + 7) / 8;
            if (valueBytes > _data.Length - _position)
            {
                throw new EncodingException("The RLE run's repeated value is truncated.");
            }

            uint value = 0;
            for (int i = 0; i < valueBytes; i++)
            {
                value |= (uint)_data[_position + i] << (8 * i);
            }

            _position += valueBytes;
            _isRepeated = true;
            _repeatedValue = value;
            _runRemaining = header >> 1;
        }

        return true;
    }

    /// <summary>Reads <paramref name="destination"/>.Length values of the current bit-packed run.</summary>
    private void ReadPacked(scoped Span<uint> destination)
    {
        int done = 0;

        // Values left over from a group that an earlier call only partly consumed.
        int buffered = _groupCount - _groupIndex;
        if (buffered > 0)
        {
            int take = Math.Min(buffered, destination.Length);
            ((Span<uint>)Group).Slice(_groupIndex, take).CopyTo(destination);
            _groupIndex += take;
            done = take;
        }

        int wholeGroups = (destination.Length - done) / 8;
        if (wholeGroups > 0)
        {
            int bytes = wholeGroups * _bitWidth;
            BitPacking.Unpack(_data.Slice(_packedPosition, bytes), destination.Slice(done, wholeGroups * 8), _bitWidth);
            _packedPosition += bytes;
            done += wholeGroups * 8;
        }

        int tail = destination.Length - done;
        if (tail > 0)
        {
            Span<uint> group = Group;
            BitPacking.Unpack(_data.Slice(_packedPosition, _bitWidth), group, _bitWidth);
            _packedPosition += _bitWidth;
            _groupCount = 8;
            group.Slice(0, tail).CopyTo(destination.Slice(done));
            _groupIndex = tail;
        }
    }

    private uint ReadVarint32()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (_position >= _data.Length)
            {
                throw new EncodingException("The RLE run header is truncated.");
            }

            byte b = _data[_position++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new EncodingException("The RLE run header is longer than 5 bytes.");
    }

    [InlineArray(8)]
    internal struct DecodedGroup
    {
        public uint Element;
    }
}
