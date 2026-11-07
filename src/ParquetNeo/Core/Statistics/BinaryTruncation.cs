using System.Buffers;
using System.Text;

namespace ParquetNeo.Core.Statistics;

/// <summary>
/// Shortens minimum and maximum values of binary columns so that long values cannot bloat the file footer. A shortened
/// minimum is a prefix (never greater than the real minimum); a shortened maximum is the prefix with its last byte, or
/// for text its last character, increased (never less than the real maximum).
/// </summary>
internal static class BinaryTruncation
{
    /// <summary>The length of the longest valid prefix of at most <paramref name="limit"/> bytes.</summary>
    public static int PrefixLength(ReadOnlySpan<byte> value, int limit, bool utf8)
    {
        if (value.Length <= limit)
        {
            return value.Length;
        }

        int length = limit;
        if (utf8)
        {
            // Do not cut a character in half: step back to the start of the one that straddles the limit.
            while (length > 0 && (value[length] & 0xC0) == 0x80)
            {
                length--;
            }
        }

        return length;
    }

    /// <summary>
    /// Builds a value no smaller than any value that starts with the first <paramref name="limit"/> bytes of
    /// <paramref name="value"/>, or returns false when none can be built (for example when those bytes are all 0xFF).
    /// </summary>
    public static bool TryCreateUpperBound(ReadOnlySpan<byte> value, int limit, bool utf8, out byte[] bound)
    {
        int length = PrefixLength(value, limit, utf8);
        var prefix = value[..length];
        if (utf8 && TryIncrementLastCharacter(prefix, limit, out bound))
        {
            return true;
        }

        if (utf8 && IsValidUtf8(prefix))
        {
            // Every character was already the largest code point.
            bound = [];
            return false;
        }

        // Not text after all (or binary data): increase the last byte that can be increased and drop the rest.
        for (int i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != 0xFF)
            {
                bound = prefix[..(i + 1)].ToArray();
                bound[i]++;
                return true;
            }
        }

        bound = [];
        return false;
    }

    private static bool TryIncrementLastCharacter(ReadOnlySpan<byte> text, int limit, out byte[] bound)
    {
        bound = [];
        int end = text.Length;
        while (end > 0)
        {
            int start = end - 1;
            while (start > 0 && (text[start] & 0xC0) == 0x80)
            {
                start--;
            }

            if (Rune.DecodeFromUtf8(text[start..end], out Rune rune, out int consumed) != OperationStatus.Done || consumed != end - start)
            {
                return false; // invalid UTF-8: the caller falls back to bytes
            }

            int next = rune.Value + 1;
            if (next is >= 0xD800 and <= 0xDFFF)
            {
                next = 0xE000; // skip the surrogates, which are not characters
            }

            // The next character can need more bytes than this one (U+07FF to U+0800); it must still fit in the limit.
            if (next <= 0x10FFFF && start + new Rune(next).Utf8SequenceLength <= limit)
            {
                var encoded = new Rune(next);
                bound = new byte[start + encoded.Utf8SequenceLength];
                text[..start].CopyTo(bound);
                encoded.EncodeToUtf8(bound.AsSpan(start));
                return true;
            }

            end = start; // this character cannot grow (within the limit): drop it and grow the one before
        }

        return false;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> text) => System.Text.Unicode.Utf8.IsValid(text);
}
