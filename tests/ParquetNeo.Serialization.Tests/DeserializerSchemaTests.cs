using ParquetNeo.DataModel;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;
using ParquetNeo.Tests;

namespace ParquetNeo.Serialization.Tests;

public class DeserializerSchemaTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream);
        return stream.ToArray();
    }

    private static List<TRow> Deserialize<TRow>(byte[] bytes) => ParquetSerializer.Deserialize<TRow>(new MemoryStream(bytes));

    // ---- the columns the row type needs ----

    private sealed class Subset
    {
        public int Int { get; set; }

        public string Name { get; set; } = "";

        [ParquetColumn("OptionalDouble")]
        public double? Value { get; set; }
    }

    [Test]
    public async Task OnlyTheColumnsOfTheRowType_AreRead()
    {
        var bytes = Serialize(Sample.Rows(10));

        var rows = Deserialize<Subset>(bytes);

        await Assert.That(rows.Select(r => r.Int)).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => i * 100_000 - 2_000_000));
        await Assert.That(rows.Select(r => r.Name)).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => $"name {i}"));
        await Assert.That(rows.Select(r => r.Value)).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => i % 5 == 2 ? (double?)null : i / 4.0));
    }

    [Test]
    public async Task TheAsyncPaths_ReadOnlyTheFooterAndTheColumnsOfTheRowType()
    {
        var bytes = Serialize(Sample.Rows(2000));
        var counting = new CountingStream(bytes);

        var rows = await ParquetSerializer.DeserializeAsync<Subset>(counting);

        await Assert.That(rows.Count).IsEqualTo(2000);
        await Assert.That(counting.BytesRead).IsLessThan(bytes.Length / 4);
        var all = new CountingStream(bytes);
        await ParquetSerializer.DeserializeAsync<Sample>(all);
        await Assert.That(all.BytesRead).IsGreaterThan(bytes.Length / 2);
        await Assert.That(all.SyncReads).IsEqualTo(0);
    }

    [Test]
    public async Task AGroupThatIsNotReachedYet_IsNotReadYet()
    {
        var bytes = Serialize(Sample.Rows(1000));
        var counting = new CountingStream(bytes);
        var options = new ParquetSerializerOptions { RowGroupSize = 100 };
        var small = new MemoryStream();
        ParquetSerializer.Serialize(Sample.Rows(1000), small, options);
        counting = new CountingStream(small.ToArray());

        await using var enumerator = ParquetSerializer.DeserializeAllAsync<Sample>(counting).GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        long afterFirst = counting.BytesRead;
        for (int i = 0; i < 100; i++)
        {
            await enumerator.MoveNextAsync();
        }

        await Assert.That(afterFirst).IsLessThan(small.Length / 2);
        await Assert.That(counting.BytesRead).IsGreaterThan(afterFirst);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _asynchronous;

        public long BytesRead { get; private set; }

        /// <summary>Reads that were not made through the asynchronous methods.</summary>
        public int SyncReads { get; private set; }

        // Every way of reading ends in this one, since the stream is a subclass of MemoryStream.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_asynchronous == 0)
            {
                SyncReads++;
            }

            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _asynchronous++;
            try
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            finally
            {
                _asynchronous--;
            }
        }
    }

    // ---- columns the file does not have ----

    private sealed class Extra
    {
        public int Int { get; set; }

        public string? Added { get; set; }

        public int? AddedNumber { get; set; }

        public Guid? AddedId { get; set; }
    }

    private sealed class Needs
    {
        public int Int { get; set; }

        public int NotInTheFile { get; set; }
    }

    [Test]
    public async Task AColumnTheFileLacks_IsNull_WhenThePropertyCanHoldNull()
    {
        var rows = Deserialize<Extra>(Serialize(Sample.Rows(5)));

        await Assert.That(rows.Count).IsEqualTo(5);
        await Assert.That(rows.Select(r => r.Int)).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => i * 100_000 - 2_000_000));
        await Assert.That(rows.All(r => r.Added is null && r.AddedNumber is null && r.AddedId is null)).IsTrue();
    }

    [Test]
    public async Task AColumnTheFileLacks_IsReported_WhenThePropertyCannotHoldNull()
    {
        var bytes = Serialize(Sample.Rows(5));

        var exception = Assert.Throws<SchemaMismatchException>(() => Deserialize<Needs>(bytes));

        await Assert.That(exception.Message).Contains("NotInTheFile");
        await Assert.That(exception.Message).Contains("Needs.NotInTheFile");
        await Assert.That(exception.Message).Contains("'Int'"); // the columns that there are
        await Assert.That(async () => await ParquetSerializer.DeserializeAsync<Needs>(new MemoryStream(bytes))).Throws<SchemaMismatchException>();
    }

    [Test]
    public async Task AFileWithNoRows_StillHasItsSchemaChecked()
    {
        var bytes = Serialize(Array.Empty<Sample>());

        await Assert.That(Deserialize<Subset>(bytes)).IsEmpty();
        await Assert.That(() => Deserialize<Needs>(bytes)).Throws<SchemaMismatchException>();
    }

    private sealed class Cased
    {
        public int INT { get; set; }

        public string? NAME { get; set; }
    }

    [Test]
    public async Task ANameThatDiffersOnlyByCase_IsTheSameColumn_ButNotWhenSeveralFit()
    {
        var rows = Deserialize<Cased>(Serialize(Sample.Rows(3)));

        await Assert.That(rows.Select(r => r.NAME!)).IsEquivalentTo(new[] { "name 0", "name 1", "name 2" });
        var ambiguous = Serialize(new[] { new Ambiguous { Name = "a", NAME = "b" } });
        var exact = Deserialize<Ambiguous>(ambiguous);
        await Assert.That(exact[0].Name).IsEqualTo("a");
        await Assert.That(exact[0].NAME).IsEqualTo("b");
        await Assert.That(Deserialize<Mixed>(ambiguous)[0].Name).IsNull(); // 'name' matches neither exactly, and two fit
    }

    private sealed class Ambiguous
    {
        public string Name { get; set; } = "";

        public string NAME { get; set; } = "";
    }

    private sealed class Mixed
    {
        [ParquetColumn("name")]
        public string? Name { get; set; }
    }

    // ---- columns of the wrong type ----

    private sealed class WrongNumbers
    {
        [ParquetColumn("Long")]
        public int Narrow { get; set; }
    }

    private sealed class WrongSign
    {
        [ParquetColumn("Int")]
        public uint Unsigned { get; set; }
    }

    private sealed class WrongKind
    {
        [ParquetColumn("Name")]
        public int Number { get; set; }
    }

    private sealed class WrongWidth
    {
        [ParquetColumn("Short")]
        public sbyte Byte { get; set; }
    }

    private sealed class WrongDate
    {
        [ParquetColumn("Day")]
        public DateTime Moment { get; set; }
    }

    private sealed class WrongText
    {
        [ParquetColumn("Id")]
        public string Text { get; set; } = "";
    }

    private sealed class WrongDecimal
    {
        [ParquetColumn("Double")]
        public decimal Money { get; set; }
    }

    private sealed class WrongEnum
    {
        [ParquetColumn("Int")]
        public DayOfWeek Day { get; set; }
    }

    [Test]
    public async Task AColumnOfAnotherType_IsReported_WithBothTypes()
    {
        var bytes = Serialize(Sample.Rows(3));
        (Action Read, string Property, string Found)[] cases =
        [
            (() => Deserialize<WrongNumbers>(bytes), "Narrow", "INT64"),
            (() => Deserialize<WrongSign>(bytes), "Unsigned", "INT32"),
            (() => Deserialize<WrongKind>(bytes), "Number", "BYTE_ARRAY"),
            (() => Deserialize<WrongWidth>(bytes), "Byte", "16-bit"),
            (() => Deserialize<WrongDate>(bytes), "Moment", "DATE"),
            (() => Deserialize<WrongText>(bytes), "Text", "FIXED_LEN_BYTE_ARRAY"),
            (() => Deserialize<WrongDecimal>(bytes), "Money", "DOUBLE"),
            (() => Deserialize<WrongEnum>(bytes), "Day", "INT32"),
        ];

        foreach (var (read, property, found) in cases)
        {
            var exception = Assert.Throws<SchemaMismatchException>(() => read());

            await Assert.That(exception.Message).Contains(property);
            await Assert.That(exception.Message).Contains(found);
        }
    }

    // ---- columns that are not flat ----

    private sealed class FlatFromNested
    {
        [ParquetColumn("person")]
        public int Person { get; set; }
    }

    private sealed class FlatFromList
    {
        [ParquetColumn("labels")]
        public string? Labels { get; set; }
    }

    [Test]
    public async Task AGroupOrAList_CannotBeReadAsASimpleType()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("golden-serializer-nested.parquet"));

        var group = Assert.Throws<SchemaMismatchException>(() => Deserialize<FlatFromNested>(bytes));
        var list = Assert.Throws<SchemaMismatchException>(() => Deserialize<FlatFromList>(bytes));

        await Assert.That(group.Message).Contains("group");
        await Assert.That(list.Message).Contains("group");
    }

    // ---- nulls the property cannot hold ----

    private sealed class RequiredButNullable
    {
        public int Int { get; set; }

        [ParquetColumn("OptionalInt")]
        public int Number { get; set; }
    }

    [Test]
    public async Task ANullForAPropertyThatCannotHoldIt_IsReported_WithTheRow()
    {
        var bytes = Serialize(Sample.Rows(10));

        var exception = Assert.Throws<ValueConversionException>(() => Deserialize<RequiredButNullable>(bytes));

        await Assert.That(exception.Message).Contains("RequiredButNullable.Number");
        await Assert.That(exception.Message).Contains("row 1"); // OptionalInt is null from row 1
    }
}
