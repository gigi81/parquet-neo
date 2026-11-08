using System.Buffers;
using System.Runtime.InteropServices;

namespace ParquetNeo.Core;

/// <summary>
/// Dictionary for 4- and 8-byte values. Floating-point values are added as their bit patterns, so that -0.0 and
/// +0.0 stay distinct and every NaN payload survives; the PLAIN bytes are the same either way.
/// </summary>
internal sealed class FixedDictionary<T> : IDictionaryBuilder where T : unmanaged, IEquatable<T>
{
    private readonly Dictionary<T, int> _indexes = new();
    private readonly List<T> _entries = new();
    private readonly PlainWriter<T> _writePlain;

    public FixedDictionary(PlainWriter<T> writePlain)
    {
        _writePlain = writePlain;
    }

    public int Count => _entries.Count;

    public long EncodedSize => (long)_entries.Count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    /// <summary>Adds the values, appending the dictionary index of each one to <paramref name="indexes"/>.</summary>
    public void Add(ReadOnlySpan<T> values, ArrayBufferWriter<int> indexes)
    {
        var output = indexes.GetSpan(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_indexes, values[i], out bool exists);
            if (!exists)
            {
                slot = _entries.Count;
                _entries.Add(values[i]);
            }

            output[i] = slot;
        }

        indexes.Advance(values.Length);
    }

    public void WritePlain(IBufferWriter<byte> output) => _writePlain(CollectionsMarshal.AsSpan(_entries), output);
}
