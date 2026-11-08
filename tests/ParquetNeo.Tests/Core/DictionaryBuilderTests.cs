using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Core;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class DictionaryBuilderTests
{
    // ---- fixed-width values ----

    private static FixedDictionary<long> Longs() => new(PlainEncoder.WriteInt64s);

    private static int[] AddAll(FixedDictionary<long> dictionary, long[] values)
    {
        var indexes = new PooledBufferWriter<int>();
        dictionary.Add(values, indexes);
        var result = indexes.WrittenSpan.ToArray();
        indexes.Dispose();
        return result;
    }

    [Test]
    public async Task FixedValues_AreNumberedInOrderOfFirstAppearance()
    {
        using var dictionary = Longs();

        var indexes = AddAll(dictionary, [50, 7, 50, -3, 7, 7, 0, long.MinValue, long.MaxValue, -3]);

        await Assert.That(indexes).IsEquivalentTo(new[] { 0, 1, 0, 2, 1, 1, 3, 4, 5, 2 });
        await Assert.That(dictionary.Count).IsEqualTo(6);
        await Assert.That(dictionary.EncodedSize).IsEqualTo(48L);
    }

    [Test]
    public async Task FixedValues_WritePlainInTheOrderTheyWereAdded()
    {
        using var dictionary = Longs();
        AddAll(dictionary, [9, 5, 9, 1]);
        var output = new ArrayBufferWriter<byte>();

        dictionary.WritePlain(output);

        var longs = new long[output.WrittenCount / 8];
        for (int i = 0; i < longs.Length; i++)
        {
            longs[i] = BinaryPrimitives.ReadInt64LittleEndian(output.WrittenSpan[(i * 8)..]);
        }

        await Assert.That(longs).IsEquivalentTo(new long[] { 9, 5, 1 });
    }

    [Test]
    public async Task ADictionaryOfManyFixedValues_GrowsAndStaysRight()
    {
        using var dictionary = Longs();
        const int Distinct = 300_000;
        var values = new long[Distinct * 2];
        for (int i = 0; i < Distinct; i++)
        {
            values[i] = i * 7919L - 1_000_000; // distinct, with negatives
        }

        for (int i = 0; i < Distinct; i++)
        {
            values[Distinct + i] = values[Distinct - 1 - i]; // all seen before, in the other order
        }

        var indexes = AddAll(dictionary, values);

        await Assert.That(dictionary.Count).IsEqualTo(Distinct);
        for (int i = 0; i < Distinct; i += 997)
        {
            await Assert.That(indexes[i]).IsEqualTo(i);
            await Assert.That(indexes[Distinct + i]).IsEqualTo(Distinct - 1 - i);
        }

        await Assert.That(indexes[^1]).IsEqualTo(Distinct - 1 - (Distinct - 1));
    }

    [Test]
    public async Task ValuesThatCollideInTheLowBits_StillGetOwnEntries()
    {
        using var dictionary = Longs();
        var values = Enumerable.Range(0, 20_000).Select(i => (long)i << 20).ToArray(); // equal in their low 20 bits

        var indexes = AddAll(dictionary, values);

        await Assert.That(dictionary.Count).IsEqualTo(20_000);
        await Assert.That(indexes).IsEquivalentTo(Enumerable.Range(0, 20_000));
    }

    [Test]
    public async Task Ints_WorkToo()
    {
        using var dictionary = new FixedDictionary<int>(PlainEncoder.WriteInt32s);
        var indexes = new PooledBufferWriter<int>();

        dictionary.Add([3, int.MinValue, 3, -1, int.MaxValue, -1], indexes);

        await Assert.That(indexes.WrittenSpan.ToArray()).IsEquivalentTo(new[] { 0, 1, 0, 2, 3, 2 });
    }

    [Test]
    public async Task ADisposedDictionary_CanBeDisposedAgain_AndTheNextOneStartsClean()
    {
        var first = Longs();
        AddAll(first, Enumerable.Range(0, 5_000).Select(i => (long)i).ToArray());
        first.Dispose();
        first.Dispose();

        // The pool hands the arrays of the first dictionary to this one: their old contents must not show.
        using var second = Longs();
        var indexes = AddAll(second, [4_999, 1, 4_999, 70_000]);

        await Assert.That(indexes).IsEquivalentTo(new[] { 0, 1, 0, 2 });
        await Assert.That(second.Count).IsEqualTo(3);
    }

    // ---- byte arrays ----

    private static int Add(BinaryDictionary dictionary, string value, out bool added) =>
        dictionary.GetOrAdd(System.Text.Encoding.UTF8.GetBytes(value), out added);

    [Test]
    public async Task ByteArrays_AreNumberedInOrderOfFirstAppearance_AndSayWhetherTheyAreNew()
    {
        using var dictionary = new BinaryDictionary(lengthPrefixed: true);

        int a = Add(dictionary, "apple", out bool addedA);
        int b = Add(dictionary, "banana", out bool addedB);
        int again = Add(dictionary, "apple", out bool addedAgain);
        int empty = Add(dictionary, "", out bool addedEmpty);
        int emptyAgain = Add(dictionary, "", out bool addedEmptyAgain);

        await Assert.That((a, b, again, empty, emptyAgain)).IsEqualTo((0, 1, 0, 2, 2));
        await Assert.That((addedA, addedB, addedAgain, addedEmpty, addedEmptyAgain)).IsEqualTo((true, true, false, true, false));
        await Assert.That(dictionary.Count).IsEqualTo(3);
        await Assert.That(dictionary.EncodedSize).IsEqualTo(5L + 4 + 6 + 4 + 0 + 4);
    }

    [Test]
    public async Task ValuesOfEveryLength_AreTelledApart()
    {
        using var dictionary = new BinaryDictionary(lengthPrefixed: true);
        // Lengths 0 to 40, with values that differ only in their first, middle or last byte.
        var values = new List<byte[]>();
        for (int length = 0; length <= 40; length++)
        {
            foreach (int changed in new[] { 0, length / 2, length - 1 }.Where(c => c >= 0 && c < length).Distinct())
            {
                var value = new byte[length];
                value[changed] = 1;
                values.Add(value);
            }

            values.Add(new byte[length]);
        }

        var first = values.Select(v => dictionary.GetOrAdd(v, out _)).ToArray();
        var second = values.Select(v => dictionary.GetOrAdd(v, out bool added) is var index && !added ? index : -1).ToArray();

        await Assert.That(first.Distinct().Count()).IsEqualTo(values.Count);
        await Assert.That(second).IsEquivalentTo(first);
    }

    [Test]
    public async Task ABinaryDictionary_WritesPlainWithOrWithoutLengths()
    {
        using var prefixed = new BinaryDictionary(lengthPrefixed: true);
        using var fixedLength = new BinaryDictionary(lengthPrefixed: false);
        foreach (var dictionary in new[] { prefixed, fixedLength })
        {
            Add(dictionary, "ab", out _);
            Add(dictionary, "cd", out _);
            Add(dictionary, "ab", out _);
        }

        var withLengths = new ArrayBufferWriter<byte>();
        var without = new ArrayBufferWriter<byte>();
        prefixed.WritePlain(withLengths);
        fixedLength.WritePlain(without);

        await Assert.That(withLengths.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 2, 0, 0, 0, (byte)'a', (byte)'b', 2, 0, 0, 0, (byte)'c', (byte)'d' });
        await Assert.That(without.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' });
    }

    [Test]
    public async Task ADictionaryOfManyByteArrays_GrowsItsTableAndArena()
    {
        using var dictionary = new BinaryDictionary(lengthPrefixed: true);
        const int Distinct = 150_000;

        for (int i = 0; i < Distinct; i++)
        {
            await Assert.That(Add(dictionary, "key-" + i + new string('x', i % 23), out bool added)).IsEqualTo(i);
            if (!added)
            {
                throw new InvalidOperationException("A new value was not new.");
            }
        }

        for (int i = 0; i < Distinct; i += 311)
        {
            await Assert.That(Add(dictionary, "key-" + i + new string('x', i % 23), out bool added)).IsEqualTo(i);
            await Assert.That(added).IsFalse();
        }

        await Assert.That(dictionary.Count).IsEqualTo(Distinct);
    }

    [Test]
    public async Task ADisposedByteArrayDictionary_CanBeDisposedAgain_AndTheNextOneStartsClean()
    {
        var first = new BinaryDictionary(lengthPrefixed: true);
        for (int i = 0; i < 3_000; i++)
        {
            Add(first, "old-" + i, out _);
        }

        first.Dispose();
        first.Dispose();

        using var second = new BinaryDictionary(lengthPrefixed: true);
        int fresh = Add(second, "old-5", out bool addedFresh);

        await Assert.That((fresh, addedFresh)).IsEqualTo((0, true));
        await Assert.That(second.Count).IsEqualTo(1);
    }

    // ---- the files made with them ----

    [Test]
    public async Task TheSameColumnWritten_AfterOthersDirtiedThePool_IsTheSameFile()
    {
        long[] ids = [.. Enumerable.Range(0, 40_000).Select(i => (long)(i % 3_000) * 31)];
        string[] names = [.. Enumerable.Range(0, 40_000).Select(i => "name-" + i % 777)];
        byte[] Write()
        {
            var stream = new MemoryStream();
            var schema = new ParquetSchema(
            [
                new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
                new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
            ]);
            using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { DataPageSize = 20_000 }, leaveOpen: true))
            using (var group = writer.AppendRowGroup())
            {
                group.NextColumn().WriteBatch(ids);
                group.NextColumn().WriteStrings(names);
            }

            return stream.ToArray();
        }

        var clean = Write();

        // Fill the pool with arrays full of other values, then write the same file again.
        for (int round = 0; round < 3; round++)
        {
            using var other = Longs();
            AddAll(other, Enumerable.Range(0, 100_000).Select(i => (long)i * 13 + 1).ToArray());
            using var strings = new BinaryDictionary(lengthPrefixed: true);
            for (int i = 0; i < 50_000; i++)
            {
                Add(strings, "zzz" + i, out _);
            }
        }

        var afterwards = Write();

        await Assert.That(afterwards).IsEquivalentTo(clean);
    }
}
