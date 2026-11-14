using System.Buffers.Binary;
using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>Collects the values of one column chunk, in the plain encoding they are written in, and makes the filter for them.</summary>
internal sealed class BloomFilterBuilder
{
    private readonly PhysicalType _type;
    private readonly int _typeLength;
    private readonly BloomFilterSettings _settings;
    private readonly HashSet64 _hashes = new();

    private BloomFilterBuilder(PrimitiveNode column, BloomFilterSettings settings)
    {
        _type = column.Type;
        _typeLength = column.TypeLength ?? 0;
        _settings = settings;
    }

    /// <summary>The builder for a column, or null when the options give it no filter.</summary>
    public static BloomFilterBuilder? Create(PrimitiveNode column, IReadOnlyDictionary<string, BloomFilterSettings>? settings) =>
        settings is not null && settings.TryGetValue(string.Join('.', column.Path), out var chosen) ? new BloomFilterBuilder(column, chosen) : null;

    /// <summary>Checks every entry of <paramref name="settings"/> against the schema.</summary>
    /// <exception cref="ArgumentException">An entry names a column that does not exist or cannot have a filter, or has settings that make no sense.</exception>
    public static void Validate(ParquetSchema schema, IReadOnlyDictionary<string, BloomFilterSettings>? settings)
    {
        if (settings is null)
        {
            return;
        }

        foreach (var (path, chosen) in settings)
        {
            var column = schema.Columns.FirstOrDefault(c => string.Join('.', c.Path) == path)
                ?? throw new ArgumentException($"ColumnBloomFilters names '{path}', which is not a column of the schema.");
            if (column.Type is PhysicalType.Boolean or PhysicalType.Int96)
            {
                throw new ArgumentException($"A {column.Type} column ('{path}') cannot have a bloom filter.");
            }

            if (chosen is null || !(chosen.FalsePositiveRate is > 0 and < 1) || chosen.MaxBytes < 32)
            {
                throw new ArgumentException($"The bloom filter of '{path}' needs a false positive rate between 0 and 1, and room for at least 32 bytes.");
            }
        }
    }

    /// <summary>Takes the values of a page or a dictionary, plain encoded.</summary>
    public void Add(ReadOnlySpan<byte> plain)
    {
        switch (_type)
        {
            case PhysicalType.Int32:
            case PhysicalType.Float:
                AddFixed(plain, sizeof(int));
                break;
            case PhysicalType.Int64:
            case PhysicalType.Double:
                AddFixed(plain, sizeof(long));
                break;
            case PhysicalType.FixedLenByteArray:
                AddFixed(plain, _typeLength);
                break;
            default:
                AddByteArrays(plain);
                break;
        }
    }

    /// <summary>The filter for every value added, as large as the settings and the number of different values call for.</summary>
    public BloomFilter Build()
    {
        var filter = BloomFilter.Create(BloomFilter.OptimalByteCount(_hashes.Count, _settings.FalsePositiveRate, _settings.MaxBytes));
        foreach (ulong hash in _hashes.Hashes())
        {
            filter.Insert(hash);
        }

        return filter;
    }

    private void AddFixed(ReadOnlySpan<byte> plain, int width)
    {
        if (width <= 0)
        {
            return;
        }

        for (int offset = 0; offset + width <= plain.Length; offset += width)
        {
            _hashes.Add(BloomFilter.HashOf(plain.Slice(offset, width)));
        }
    }

    private void AddByteArrays(ReadOnlySpan<byte> plain)
    {
        int offset = 0;
        while (plain.Length - offset >= sizeof(int))
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(plain[offset..]);
            offset += sizeof(int);
            if (length < 0 || length > plain.Length - offset)
            {
                return;
            }

            _hashes.Add(BloomFilter.HashOf(plain.Slice(offset, length)));
            offset += length;
        }
    }
}
