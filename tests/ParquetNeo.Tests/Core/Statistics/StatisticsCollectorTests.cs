using System.Text;
using ParquetNeo.Core.Statistics;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Core.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = ParquetNeo.Metadata.Statistics;

public class StatisticsCollectorTests
{
    // ---- the vectorized minimum / maximum ----

    [Test]
    public async Task MinMax_MatchesAPlainLoop_ForEveryLengthAndType()
    {
        var random = new Random(7);
        var failures = new List<string>();
        for (int length = 1; length <= 80; length++)
        {
            var ints = Enumerable.Range(0, length).Select(_ => random.Next(int.MinValue, int.MaxValue)).ToArray();
            var longs = Enumerable.Range(0, length).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).ToArray();
            var doubles = Enumerable.Range(0, length).Select(_ => (random.NextDouble() - 0.5) * 1e6).ToArray();
            var floats = Enumerable.Range(0, length).Select(_ => (float)((random.NextDouble() - 0.5) * 1e6)).ToArray();

            Check<int>(failures, ints);
            Check<uint>(failures, ints.Select(i => unchecked((uint)i)).ToArray());
            Check<long>(failures, longs);
            Check<ulong>(failures, longs.Select(i => unchecked((ulong)i)).ToArray());
            Check<double>(failures, doubles);
            Check<float>(failures, floats);
        }

        await Assert.That(failures).IsEmpty();
    }

    private static void Check<T>(List<string> failures, T[] values) where T : unmanaged, System.Numerics.INumber<T>
    {
        bool ok = VectorMinMax.TryGetMinMax<T>(values, out T min, out T max);
        if (!ok || min != values.Min() || max != values.Max())
        {
            failures.Add($"{typeof(T).Name} x{values.Length}: got ({min}, {max}), expected ({values.Min()}, {values.Max()})");
        }
    }

    [Test]
    public async Task MinMax_FindsTheExtremeAtAnyPosition()
    {
        for (int length = 1; length <= 40; length++)
        {
            for (int position = 0; position < length; position++)
            {
                var low = new int[length];
                var high = new int[length];
                low[position] = int.MinValue;
                high[position] = int.MaxValue;
                Array.Fill(low, 1, 0, length);
                low[position] = int.MinValue;
                Array.Fill(high, 1, 0, length);
                high[position] = int.MaxValue;

                VectorMinMax.TryGetMinMax<int>(low, out int min, out _);
                VectorMinMax.TryGetMinMax<int>(high, out _, out int max);

                await Assert.That(min).IsEqualTo(int.MinValue).Because($"length {length} position {position}");
                await Assert.That(max).IsEqualTo(int.MaxValue).Because($"length {length} position {position}");
            }
        }
    }

    [Test]
    public async Task MinMax_RefusesFloatingPointSpansWithNaNs_WhereverTheyAre()
    {
        for (int length = 1; length <= 40; length++)
        {
            for (int position = 0; position < length; position++)
            {
                var values = new double[length];
                values[position] = double.NaN;

                await Assert.That(VectorMinMax.TryGetMinMax<double>(values, out _, out _)).IsFalse().Because($"length {length} position {position}");
            }
        }
    }

    // ---- scalar columns ----

    private static Statistics Ints(bool unsigned, params int[][] batches)
    {
        if (unsigned)
        {
            var collector = new ScalarStatistics<int, uint>();
            foreach (var batch in batches) collector.Add<int>(batch);
            return collector.Build(0);
        }
        else
        {
            var collector = new ScalarStatistics<int, int>();
            foreach (var batch in batches) collector.Add<int>(batch);
            return collector.Build(0);
        }
    }

    [Test]
    public async Task SignedIntegers_UseSignedOrder_AcrossBatches()
    {
        var statistics = Ints(false, [5, -3, 9], [int.MinValue], [100, int.MaxValue, 0]);

        await Assert.That(BitConverter.ToInt32(statistics.MinValue!)).IsEqualTo(int.MinValue);
        await Assert.That(BitConverter.ToInt32(statistics.MaxValue!)).IsEqualTo(int.MaxValue);
        await Assert.That(statistics.IsMinValueExact).IsTrue();
        await Assert.That(statistics.IsMaxValueExact).IsTrue();
        await Assert.That(statistics.NullCount).IsEqualTo(0L);
        await Assert.That(statistics.NanCount).IsNull();
    }

    [Test]
    public async Task UnsignedIntegers_UseUnsignedOrder_ButKeepTheirBits()
    {
        // 0xFFFFFFFF is stored as -1 and is the largest unsigned value; 0x80000000 (int.MinValue) is bigger than 5.
        var statistics = Ints(true, [5, -1, 7, int.MinValue, 0]);

        await Assert.That(BitConverter.ToInt32(statistics.MinValue!)).IsEqualTo(0);
        await Assert.That(BitConverter.ToInt32(statistics.MaxValue!)).IsEqualTo(-1);
    }

    [Test]
    public async Task UnsignedLongs_UseUnsignedOrder()
    {
        var collector = new ScalarStatistics<long, ulong>();
        collector.Add<long>([5L, -1L, long.MinValue, long.MaxValue]);

        var statistics = collector.Build(0);

        await Assert.That(BitConverter.ToInt64(statistics.MinValue!)).IsEqualTo(5L);
        await Assert.That(BitConverter.ToInt64(statistics.MaxValue!)).IsEqualTo(-1L);
    }

    [Test]
    public async Task NoValues_GiveOnlyTheCounts()
    {
        var statistics = new ScalarStatistics<long, long>().Build(12);

        await Assert.That(statistics.NullCount).IsEqualTo(12L);
        await Assert.That(statistics.MinValue).IsNull();
        await Assert.That(statistics.MaxValue).IsNull();
        await Assert.That(statistics.IsMinValueExact).IsNull();
    }

    [Test]
    public async Task EmptyBatches_AreIgnored()
    {
        var collector = new ScalarStatistics<int, int>();
        collector.Add<int>([]);
        collector.Add<int>([3]);
        collector.Add<int>([]);

        await Assert.That(BitConverter.ToInt32(collector.Build(0).MinValue!)).IsEqualTo(3);
    }

    // ---- floating point ----

    private static Statistics Doubles(params double[][] batches)
    {
        var collector = new ScalarStatistics<double, double>();
        foreach (var batch in batches) collector.Add<double>(batch);
        return collector.Build(0);
    }

    [Test]
    public async Task NaNs_AreCounted_AndLeftOutOfTheBounds()
    {
        var statistics = Doubles([double.NaN, 3.5, -2.0], [double.NaN, 10.0, double.NaN]);

        await Assert.That(BitConverter.ToDouble(statistics.MinValue!)).IsEqualTo(-2.0);
        await Assert.That(BitConverter.ToDouble(statistics.MaxValue!)).IsEqualTo(10.0);
        await Assert.That(statistics.NanCount).IsEqualTo(3L);
    }

    [Test]
    public async Task WhenEveryValueIsNaN_ThereAreNoBounds()
    {
        var statistics = Doubles([double.NaN, double.NaN]);

        await Assert.That(statistics.MinValue).IsNull();
        await Assert.That(statistics.MaxValue).IsNull();
        await Assert.That(statistics.NanCount).IsEqualTo(2L);
    }

    [Test]
    public async Task TheNaNCount_IsWrittenEvenWhenThereAreNone()
    {
        var statistics = Doubles([1.0, 2.0]);

        await Assert.That(statistics.NanCount).IsEqualTo(0L);
    }

    [Test]
    public async Task ZeroBounds_AreNormalized_WhateverSignWasSeen()
    {
        // A minimum of zero is written as -0.0 and a maximum of zero as +0.0.
        foreach (var batch in new[] { new[] { 0.0, -0.0 }, new[] { -0.0, 0.0 }, new[] { 0.0 }, new[] { -0.0 } })
        {
            var statistics = Doubles(batch);

            await Assert.That(BitConverter.DoubleToInt64Bits(BitConverter.ToDouble(statistics.MinValue!))).IsEqualTo(BitConverter.DoubleToInt64Bits(-0.0));
            await Assert.That(BitConverter.DoubleToInt64Bits(BitConverter.ToDouble(statistics.MaxValue!))).IsEqualTo(BitConverter.DoubleToInt64Bits(0.0));
        }

        var mixed = Doubles([-0.0, 5.0]);
        await Assert.That(BitConverter.DoubleToInt64Bits(BitConverter.ToDouble(mixed.MinValue!))).IsEqualTo(BitConverter.DoubleToInt64Bits(-0.0));
        await Assert.That(BitConverter.ToDouble(mixed.MaxValue!)).IsEqualTo(5.0);
        var negative = Doubles([-5.0, -0.0]);
        await Assert.That(BitConverter.DoubleToInt64Bits(BitConverter.ToDouble(negative.MaxValue!))).IsEqualTo(BitConverter.DoubleToInt64Bits(0.0));
    }

    [Test]
    public async Task Infinities_AreValidBounds()
    {
        var statistics = Doubles([double.NegativeInfinity, 1.0, double.PositiveInfinity]);

        await Assert.That(BitConverter.ToDouble(statistics.MinValue!)).IsEqualTo(double.NegativeInfinity);
        await Assert.That(BitConverter.ToDouble(statistics.MaxValue!)).IsEqualTo(double.PositiveInfinity);
    }

    [Test]
    public async Task SinglePrecision_UsesFourByteBounds()
    {
        var collector = new ScalarStatistics<float, float>();
        collector.Add<float>([1.5f, float.NaN, -4.25f]);

        var statistics = collector.Build(2);

        await Assert.That(statistics.MinValue!.Length).IsEqualTo(4);
        await Assert.That(BitConverter.ToSingle(statistics.MinValue)).IsEqualTo(-4.25f);
        await Assert.That(BitConverter.ToSingle(statistics.MaxValue!)).IsEqualTo(1.5f);
        await Assert.That(statistics.NanCount).IsEqualTo(1L);
        await Assert.That(statistics.NullCount).IsEqualTo(2L);
    }

    // ---- booleans ----

    [Test]
    [Arguments(new[] { true, true }, (byte)1, (byte)1)]
    [Arguments(new[] { false, false }, (byte)0, (byte)0)]
    [Arguments(new[] { true, false }, (byte)0, (byte)1)]
    public async Task Booleans_OrderFalseBeforeTrue(bool[] values, byte min, byte max)
    {
        var collector = new BooleanStatistics();
        collector.Add<bool>(values);

        var statistics = collector.Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(new[] { min });
        await Assert.That(statistics.MaxValue).IsEquivalentTo(new[] { max });
    }

    // ---- binary ----

    private static BinaryStatistics Binary(BinaryOrder order, bool utf8, int truncate, params byte[][] values)
    {
        var collector = new BinaryStatistics(order, utf8, truncate);
        foreach (var value in values) collector.AddBinary(value);
        return collector;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public async Task Binary_ComparesBytesAsUnsigned()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 64, [0x7F], [0x80], [0xFF, 0x00], [0x00, 0xFF], []).Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(Array.Empty<byte>());
        await Assert.That(statistics.MaxValue).IsEquivalentTo(new byte[] { 0xFF, 0x00 });
    }

    [Test]
    public async Task Binary_ALongerValueWithTheSamePrefixIsGreater()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 64, [1, 2], [1, 2, 0], [1]).Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(new byte[] { 1 });
        await Assert.That(statistics.MaxValue).IsEquivalentTo(new byte[] { 1, 2, 0 });
    }

    [Test]
    public async Task Strings_OrderByUtf8Bytes_NotByUtf16()
    {
        // "é" is 0xC3 0xA9 and sorts after "z"; a U+FFFF character (0xEF 0xBF 0xBF) sorts after both.
        var statistics = Binary(BinaryOrder.UnsignedBytes, true, 64, Utf8("z"), Utf8("é"), Utf8("￿"), Utf8("Z")).Build(0);

        await Assert.That(Encoding.UTF8.GetString(statistics.MinValue!)).IsEqualTo("Z");
        await Assert.That(Encoding.UTF8.GetString(statistics.MaxValue!)).IsEqualTo("￿");
    }

    [Test]
    public async Task ShortValues_AreStoredWhole_AndExact()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, true, 8, Utf8("abcdefgh"), Utf8("b")).Build(0);

        await Assert.That(statistics.IsMinValueExact).IsTrue();
        await Assert.That(statistics.IsMaxValueExact).IsTrue();
    }

    [Test]
    public async Task LongMinimums_AreCutToAPrefix_AndLongMaximumsGrow()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, true, 4, Utf8("abcdefgh"), Utf8("mnopqrst")).Build(0);

        await Assert.That(Encoding.UTF8.GetString(statistics.MinValue!)).IsEqualTo("abcd");
        await Assert.That(statistics.IsMinValueExact).IsFalse();
        await Assert.That(Encoding.UTF8.GetString(statistics.MaxValue!)).IsEqualTo("mnoq"); // "mnop" with the last letter increased
        await Assert.That(statistics.IsMaxValueExact).IsFalse();
    }

    [Test]
    public async Task Truncation_NeverSplitsACharacter()
    {
        // "aé" is 3 bytes (0x61 0xC3 0xA9): a limit of 2 would cut é in half.
        var statistics = Binary(BinaryOrder.UnsignedBytes, true, 2, Utf8("aéz"), Utf8("aéy")).Build(0);

        await Assert.That(Encoding.UTF8.GetString(statistics.MinValue!)).IsEqualTo("a");
        await Assert.That(Encoding.UTF8.GetString(statistics.MaxValue!)).IsEqualTo("b");
    }

    [Test]
    public async Task ABoundIsKept_EvenWhenALaterValueTiesWithItsPrefix()
    {
        // The stored minimum "abc" is a prefix of "abcdef"; a later "abcd" is above it, so the minimum stays valid.
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 3, Utf8("abcdef"), Utf8("abcd")).Build(0);

        await Assert.That(Encoding.UTF8.GetString(statistics.MinValue!)).IsEqualTo("abc");
        await Assert.That(Encoding.UTF8.GetString(statistics.MaxValue!)).IsEqualTo("abd");
    }

    [Test]
    public async Task AMaximumThatCannotGrow_IsLeftOut()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 2, [0xFF, 0xFF, 0x01], [0x01]).Build(0);

        await Assert.That(statistics.MaxValue).IsNull();
        await Assert.That(statistics.IsMaxValueExact).IsNull();
        await Assert.That(statistics.MinValue).IsEquivalentTo(new byte[] { 0x01 });
    }

    [Test]
    public async Task AMaximumThatCannotGrow_StaysLeftOut_EvenIfSmallerValuesFollow()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 2, [0xFF, 0xFF, 0x01], [0x02], [0xFF, 0xFF, 0x05]).Build(0);

        await Assert.That(statistics.MaxValue).IsNull();
    }

    [Test]
    public async Task Binary_WithAZeroLimit_StillWorks()
    {
        var statistics = Binary(BinaryOrder.UnsignedBytes, false, 0, [5, 6, 7], [1, 2, 3]).Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(new byte[] { 1 });
        await Assert.That(statistics.MaxValue).IsEquivalentTo(new byte[] { 6 });
    }

    [Test]
    public async Task TruncatedBounds_AlwaysBracketTheData_ForRandomText()
    {
        var random = new Random(11);
        string alphabet = "abcxyzéÿࠀ퟿￿\U0001F600\U0010FFFF";
        var runes = alphabet.EnumerateRunes().ToArray();
        for (int round = 0; round < 300; round++)
        {
            int limit = random.Next(1, 12);
            var values = Enumerable.Range(0, random.Next(1, 20))
                .Select(_ => string.Concat(Enumerable.Range(0, random.Next(0, 14)).Select(_ => runes[random.Next(runes.Length)].ToString())))
                .Select(Utf8)
                .ToArray();
            var statistics = Binary(BinaryOrder.UnsignedBytes, true, limit, values).Build(0);

            var min = values.MinBy(v => v, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))!;
            var max = values.MaxBy(v => v, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))!;

            await Assert.That(statistics.MinValue!.AsSpan().SequenceCompareTo(min) <= 0).IsTrue().Because($"min, round {round}");
            await Assert.That(statistics.MinValue!.Length <= Math.Max(limit, min.Length)).IsTrue();
            await Assert.That(statistics.MinValue.Length <= limit || statistics.MinValue.SequenceEqual(min)).IsTrue();
            await Assert.That(Encoding.UTF8.GetCharCount(statistics.MinValue)).IsGreaterThanOrEqualTo(0);
            await Assert.That(System.Text.Unicode.Utf8.IsValid(statistics.MinValue)).IsTrue().Because($"min is text, round {round}");
            if (statistics.MaxValue is not null)
            {
                await Assert.That(statistics.MaxValue.AsSpan().SequenceCompareTo(max) >= 0).IsTrue().Because($"max, round {round}");
                await Assert.That(statistics.MaxValue.Length <= limit || statistics.MaxValue.SequenceEqual(max)).IsTrue();
                await Assert.That(System.Text.Unicode.Utf8.IsValid(statistics.MaxValue)).IsTrue().Because($"max is text, round {round}");
            }
        }
    }

    // ---- decimals ----

    [Test]
    public async Task Decimals_CompareAsSignedBigEndianNumbers()
    {
        // As numbers: 0xFF00 = -256, 0x8000 = -32768, 0x7F00 = 32512, 0x0001 = 1.
        var statistics = Binary(BinaryOrder.SignedBigEndian, false, 64, [0xFF, 0x00], [0x80, 0x00], [0x7F, 0x00], [0x00, 0x01]).Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(new byte[] { 0x80, 0x00 });
        await Assert.That(statistics.MaxValue).IsEquivalentTo(new byte[] { 0x7F, 0x00 });
    }

    [Test]
    [Arguments(new byte[] { 0xFF }, new byte[] { 0x00 }, -1)] // -1 < 0
    [Arguments(new byte[] { 0x00 }, new byte[] { 0xFF }, 1)]
    [Arguments(new byte[] { 0xFF }, new byte[] { 0xFF, 0xFF }, 0)] // -1 == -1 in two bytes
    [Arguments(new byte[] { 0x01 }, new byte[] { 0x00, 0x01 }, 0)] // 1 == 1 in two bytes
    [Arguments(new byte[] { 0xFE }, new byte[] { 0xFF, 0xFF }, -1)] // -2 < -1
    [Arguments(new byte[] { 0x7F }, new byte[] { 0x00, 0x80 }, -1)] // 127 < 128
    [Arguments(new byte[] { }, new byte[] { 0x00 }, 0)] // an empty number is 0
    [Arguments(new byte[] { }, new byte[] { 0xFF }, 1)]
    [Arguments(new byte[] { 0x80, 0x00, 0x00 }, new byte[] { 0x80, 0x00 }, -1)] // -8388608 < -32768
    public async Task SignedComparison_ExtendsShorterNumbers(byte[] left, byte[] right, int expectedSign)
    {
        await Assert.That(Math.Sign(BinaryStatistics.CompareSigned(left, right))).IsEqualTo(expectedSign);
        await Assert.That(Math.Sign(BinaryStatistics.CompareSigned(right, left))).IsEqualTo(-expectedSign);
    }

    [Test]
    public async Task Decimals_AreNeverTruncated()
    {
        var statistics = Binary(BinaryOrder.SignedBigEndian, false, 2, [1, 2, 3, 4, 5], [9, 8, 7, 6, 5]).Build(0);

        await Assert.That(statistics.MinValue).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5 });
        await Assert.That(statistics.IsMinValueExact).IsTrue();
    }

    // ---- truncation helpers ----

    [Test]
    [Arguments("abcdef", 3, false, "abd")]
    [Arguments("ab", 3, false, "ab")]
    public async Task UpperBound_IncreasesTheLastByte(string text, int limit, bool utf8, string expected)
    {
        // A value that is not longer than the limit is not shortened by the collector, but the helper still increments.
        bool ok = BinaryTruncation.TryCreateUpperBound(Utf8(text), limit, utf8, out var bound);

        await Assert.That(ok).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(bound)).IsEqualTo(expected == "ab" ? "ac" : expected);
    }

    [Test]
    public async Task UpperBound_SkipsTrailingFFBytes()
    {
        bool ok = BinaryTruncation.TryCreateUpperBound([0x01, 0xFF, 0xFF, 0x05], 3, false, out var bound);

        await Assert.That(ok).IsTrue();
        await Assert.That(bound).IsEquivalentTo(new byte[] { 0x02 });
    }

    [Test]
    public async Task UpperBound_OfAllFFBytes_DoesNotExist()
    {
        await Assert.That(BinaryTruncation.TryCreateUpperBound([0xFF, 0xFF, 0xFF], 2, false, out _)).IsFalse();
    }

    [Test]
    public async Task UpperBound_OfText_StepsOverSurrogatesAndTheLastCodePoint()
    {
        // U+D7FF is followed by the surrogates, which are not characters: the next one is U+E000.
        BinaryTruncation.TryCreateUpperBound(Utf8("a퟿zz"), 4, true, out var surrogates);
        // U+10FFFF cannot grow, so it is dropped and the character before it grows.
        BinaryTruncation.TryCreateUpperBound(Utf8("a\U0010FFFFx"), 5, true, out var largest);
        bool allLargest = BinaryTruncation.TryCreateUpperBound(Utf8("\U0010FFFF\U0010FFFFz"), 8, true, out _);

        await Assert.That(Encoding.UTF8.GetString(surrogates)).IsEqualTo("a");
        await Assert.That(Encoding.UTF8.GetString(largest)).IsEqualTo("b");
        await Assert.That(allLargest).IsFalse();
    }

    [Test]
    public async Task UpperBound_OfText_NeverExceedsTheLimit()
    {
        // U+07FF is 2 bytes and its successor U+0800 is 3: with a limit of 3 and "a" before it, the grown character would not fit.
        bool ok = BinaryTruncation.TryCreateUpperBound(Utf8("a\u07FFz"), 3, true, out var bound);

        await Assert.That(ok).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(bound)).IsEqualTo("b");
        await Assert.That(bound.Length).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task UpperBound_OfInvalidUtf8_FallsBackToBytes()
    {
        bool ok = BinaryTruncation.TryCreateUpperBound([0x61, 0xC3, 0x28, 0x62], 3, true, out var bound);

        await Assert.That(ok).IsTrue();
        await Assert.That(bound.AsSpan().SequenceCompareTo(new byte[] { 0x61, 0xC3, 0x28, 0x62 }) > 0).IsTrue();
    }

    [Test]
    [Arguments("hello", 10, 5)]
    [Arguments("hello", 3, 3)]
    [Arguments("aéb", 2, 1)] // the limit falls inside é
    [Arguments("aéb", 3, 3)]
    [Arguments("😀😀", 5, 4)] // 4-byte characters
    [Arguments("é", 1, 0)]
    public async Task PrefixLength_StopsAtACharacterBoundary(string text, int limit, int expected)
    {
        await Assert.That(BinaryTruncation.PrefixLength(Utf8(text), limit, utf8: true)).IsEqualTo(expected);
    }

    [Test]
    public async Task PrefixLength_ForBinary_IsJustTheLimit()
    {
        await Assert.That(BinaryTruncation.PrefixLength([1, 0x80, 0x80, 4], 2, utf8: false)).IsEqualTo(2);
    }
}
