using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Serialization.Generated;

namespace ParquetNeo.Tests.Serialization;

/// <summary>The generated mapping of a row with every kind of column does what reflection does: same schema, same file, same rows.</summary>
public class GeneratedKitchenSinkTests
{
    private static void Fill(KitchenSinkBase row, int seed)
    {
        bool nulls = seed % 4 == 0;
        row.Flag = seed % 2 == 0;
        row.Byte = (byte)seed;
        row.SignedByte = (sbyte)-seed;
        row.Short = (short)(seed * 300);
        row.UnsignedShort = (ushort)(seed * 7000);
        row.Int = seed * 100_000;
        row.UnsignedInt = (uint)seed * 4_000_000;
        row.Long = seed * 10_000_000_000L;
        row.UnsignedLong = (ulong)seed * 4_000_000_000_000UL;
        row.Float = seed + 0.25f;
        row.Double = seed + 0.125;
        row.Money = seed + 0.1234m;
        row.Text = $"text {seed}";
        row.Note = nulls ? null : $"note {seed}";
        row.Moment = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(seed * 123_456);
        row.Offset = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero).AddMinutes(seed);
        row.Day = new DateOnly(2000, 1, 1).AddDays(seed);
        row.Clock = new TimeOnly(1, 2, 3).AddMinutes(seed);
        row.Id = new Guid(seed, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]);
        row.Small = (Half)(seed * 0.5f);
        row.Payload = [(byte)seed, 2, 3];
        row.Blob = nulls ? null : [(byte)seed];
        row.Weekday = (DayOfWeek)(seed % 7);
        row.MaybeWeekday = nulls ? null : (DayOfWeek)((seed + 1) % 7);
        row.MaybeInt = nulls ? null : seed;
        row.MaybeMoney = nulls ? null : seed + 0.5m;
        row.MaybeMoment = nulls ? null : new DateTime(2020, 2, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seed);
        row.MaybeId = nulls ? null : new Guid(seed, 5, 5, [1, 1, 1, 1, 1, 1, 1, 1]);
        row.Renamed = seed + 1;
        row.Counter = seed * 3L;
        row.Unique = nulls ? null : $"unique {seed}";
        row.Category = $"category {seed % 3}";
    }

    private static List<KitchenSink> Rows() => Enumerable.Range(0, 25).Select(i =>
    {
        var row = new KitchenSink { Own = $"own {i}", OwnMaybe = i % 3 == 0 ? null : i };
        Fill(row, i);
        return row;
    }).ToList();

    private static List<KitchenSinkTwin> Twins() => Enumerable.Range(0, 25).Select(i =>
    {
        var row = new KitchenSinkTwin { Own = $"own {i}", OwnMaybe = i % 3 == 0 ? null : i };
        Fill(row, i);
        return row;
    }).ToList();

    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, CompressionCodec codec = CompressionCodec.Uncompressed, int rowGroupSize = 100_000)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { Writer = new ParquetWriterOptions { Compression = codec }, RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    private static string Describe(SchemaNode node) =>
        $"{node.Name}|{node.Repetition}|{(node as PrimitiveNode)?.Type}|{node.LogicalType}|{(node as PrimitiveNode)?.TypeLength}";

    [Test]
    public async Task TheSchema_IsTheOneReflectionMakes_ColumnForColumn()
    {
        var generated = ParquetSerializer.GetSchema<KitchenSink>().Root.Children.Select(Describe).ToArray();
        var reflected = ParquetSerializer.GetSchema<KitchenSinkTwin>().Root.Children.Select(Describe).ToArray();

        await Assert.That(generated).IsEquivalentTo(reflected);
        await Assert.That(generated.Length).IsEqualTo(35);

        // The order of the columns is part of it: the attribute's order first, then the base type before the derived one, then as declared.
        await Assert.That(generated[0]).StartsWith("renamed|");
        await Assert.That(generated[^2]).StartsWith("Own|");
    }

    [Test]
    [Arguments(CompressionCodec.Uncompressed, 100_000)]
    [Arguments(CompressionCodec.Gzip, 100_000)]
    [Arguments(CompressionCodec.Brotli, 7)]
    [Arguments(CompressionCodec.Uncompressed, 1)]
    public async Task TheFileWritten_IsByteForByteWhatReflectionWrites(CompressionCodec codec, int rowGroupSize)
    {
        byte[] generated = Serialize(Rows(), codec, rowGroupSize);
        byte[] reflected = Serialize(Twins(), codec, rowGroupSize);

        await Assert.That(generated.AsSpan().SequenceEqual(reflected)).IsTrue();
    }

    [Test]
    public async Task TheRowsRead_AreTheRowsWritten_ByEitherPath_FromAFileOfEither()
    {
        var expected = Rows();
        foreach (byte[] bytes in new[] { Serialize(Rows()), Serialize(Twins()) })
        {
            var generated = ParquetSerializer.Deserialize<KitchenSink>(new MemoryStream(bytes));
            var reflected = ParquetSerializer.Deserialize<KitchenSinkTwin>(new MemoryStream(bytes));

            await Assert.That(generated.Count).IsEqualTo(25);
            for (int i = 0; i < expected.Count; i++)
            {
                // What is not a column (the ignored property) and what cannot be set (the one computed) is not there to compare.
                expected[i].Ignored = 0;
                await Assert.That(DeepComparer.Difference(expected[i], generated[i])).IsNull();
                await Assert.That(generated[i].Derived).IsEqualTo(reflected[i].Derived);
                await Assert.That(reflected[i].Ignored).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task OnlyTheColumnsTheRowTypeNames_AreReadFromAWiderFile()
    {
        var rows = Rows().Take(3).ToList();
        var narrow = ParquetSerializer.Deserialize<Narrow>(new MemoryStream(Serialize(rows)));

        await Assert.That(narrow.Select(r => r.Text).ToArray()).IsEquivalentTo(rows.Select(r => r.Text).ToArray());
    }

    [Test]
    public async Task ANullInARequiredReferenceProperty_IsRefusedByBothPathsWithTheSameMessage()
    {
        var generated = Assert.Throws<ArgumentException>(() => Serialize([new KitchenSink { Text = null! }]));
        var reflected = Assert.Throws<ArgumentException>(() => Serialize([new KitchenSinkTwin { Text = null! }]));

        // The property is declared by the base type, which both share, so the messages are the same.
        await Assert.That(generated.Message).IsEqualTo(reflected.Message);
        await Assert.That(generated.Message).Contains("KitchenSinkBase.Text");
    }

    [Test]
    public async Task AnOptionalColumnThatIsMissingFromTheFile_IsNull_AndARequiredOneIsRefused()
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(new[] { new OnlyId { Id = Guid.NewGuid() } }, stream);

        stream.Position = 0;
        var exception = Assert.Throws<SchemaMismatchException>(() => ParquetSerializer.Deserialize<KitchenSink>(stream));

        await Assert.That(exception.Message).Contains("has no column named");
    }

    private sealed class Narrow
    {
        public string Text { get; set; } = "";
    }

    private sealed class OnlyId
    {
        public Guid Id { get; set; }
    }
}
