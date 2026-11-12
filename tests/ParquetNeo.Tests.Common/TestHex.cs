namespace ParquetNeo.Tests;

/// <summary>Hexadecimal text for hand-written byte vectors.</summary>
public static class TestHex
{
    /// <summary>Parses "15 02 00" style hex.</summary>
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    public static string Format(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
