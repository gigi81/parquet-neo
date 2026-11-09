using System.Data;
using ParquetNeo.Core;
using ParquetNeo.Data;
using ParquetNeo.DataModel;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

/// <summary>Encoding the columns of a row group on several threads: the file must be the one the sequential writer makes.</summary>
public class ParallelEncodingTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions options)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    private static ParquetSerializerOptions Options(int degree, ParquetWriterOptions? writer = null, int rowGroupSize = 100_000) =>
        new() { MaxDegreeOfParallelism = degree, RowGroupSize = rowGroupSize, Writer = writer ?? new ParquetWriterOptions() };

    [Test]
    [Arguments(CompressionCodec.Uncompressed, true, DataPageVersion.V1)]
    [Arguments(CompressionCodec.Gzip, true, DataPageVersion.V1)]
    [Arguments(CompressionCodec.Gzip, false, DataPageVersion.V2)]
    [Arguments(CompressionCodec.Uncompressed, false, DataPageVersion.V1)]
    public async Task TheFile_IsTheSameWhateverTheDegreeOfParallelism(CompressionCodec codec, bool dictionary, DataPageVersion version)
    {
        var rows = Sample.Rows(1_500);
        var writer = new ParquetWriterOptions { Compression = codec, DictionaryEnabled = dictionary, DataPageVersion = version, DataPageSize = 2048 };

        var sequential = Serialize(rows, Options(1, writer, rowGroupSize: 600));
        var parallel = Serialize(rows, Options(3, writer, rowGroupSize: 600));

        await Assert.That(parallel).IsEquivalentTo(sequential);
    }

    [Test]
    public async Task NestedColumns_AreTheSameToo()
    {
        var rows = Document.Rows(400);

        var sequential = Serialize(rows, Options(1, rowGroupSize: 150));
        var parallel = Serialize(rows, Options(3, rowGroupSize: 150));

        await Assert.That(parallel).IsEquivalentTo(sequential);
    }

    [Test]
    public async Task AFileWrittenInParallel_ReadsBack()
    {
        var rows = Sample.Rows(1_500);

        var bytes = Serialize(rows, Options(3, rowGroupSize: 500));

        using var file = ReadHelper.Open(bytes);
        await Assert.That(file.RowGroupCount).IsEqualTo(3);
        await Assert.That(file.RowCount).IsEqualTo(1_500);
        var back = ParquetSerializer.Deserialize<Sample>(new MemoryStream(bytes));
        await Assert.That(back.Count).IsEqualTo(1_500);
        await Assert.That(back[1234].Name).IsEqualTo(rows[1234].Name);
    }

    [Test]
    public async Task TheAsynchronousSerializer_IsTheSameToo()
    {
        var rows = Sample.Rows(1_200);
        var sequential = Serialize(rows, Options(1, rowGroupSize: 500));
        var stream = new MemoryStream();

        await ParquetSerializer.SerializeAsync(rows, stream, Options(3, rowGroupSize: 500));

        await Assert.That(stream.ToArray()).IsEquivalentTo(sequential);
    }

    public sealed class Priced
    {
        public int Id { get; set; }

        [ParquetDecimal(5, 2)]
        public decimal Price { get; set; }

        public string? Name { get; set; }
    }

    [Test]
    public async Task AnErrorWhileEncoding_ReachesTheCallerAsItIs()
    {
        List<Priced> rows = [new() { Id = 1, Price = 1.5m, Name = "a" }, new() { Id = 2, Price = 1_234_567.89m, Name = "b" }];

        await Assert.That(() => Serialize(rows, Options(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Serialize(rows, Options(3))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ADegreeOfParallelismBelowOne_IsRefused()
    {
        await Assert.That(() => Serialize(Sample.Rows(3), Options(0))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Serialize(Sample.Rows(3), Options(-1))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ASingleColumn_IsWrittenSequentiallyWhateverTheOption()
    {
        List<SingleColumn> rows = [.. Enumerable.Range(0, 100).Select(i => new SingleColumn { Value = i })];

        var bytes = Serialize(rows, Options(3));

        await Assert.That(bytes).IsEquivalentTo(Serialize(rows, Options(1)));
    }

    public sealed class SingleColumn
    {
        public int Value { get; set; }
    }

    // ---- reading a file that lies about its size ----

    [Test]
    public async Task AFooterThatClaimsAHugeNumberOfRows_IsReportedAndNotAllocated()
    {
        var bytes = Serialize(Sample.Rows(50), Options(1));
        var lying = FooterEditor.Rewrite(bytes, f =>
        {
            f.NumRows = 2_000_000_000;
            f.RowGroups[0].NumRows = 2_000_000_000;
            foreach (var column in f.RowGroups[0].Columns)
            {
                column.MetaData!.NumValues = 2_000_000_000;
            }
        });

        // Whatever the file is reported as, it must not try to make room for two billion rows first.
        var exception = Assert.Throws<ParquetNeo.Exceptions.ParquetException>(() => ParquetSerializer.Deserialize<Sample>(new MemoryStream(lying)));

        await Assert.That(exception).IsNotNull();
    }

    // ---- the data writer ----

    [Test]
    public async Task TheDataWriter_WritesTheSameFileInParallel()
    {
        DataTable Table()
        {
            var table = new DataTable();
            table.Columns.Add("id", typeof(long));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("score", typeof(double));
            for (int i = 0; i < 2_500; i++)
            {
                table.Rows.Add((long)i, i % 7 == 0 ? DBNull.Value : "name-" + (i % 100), i % 3 == 0 ? DBNull.Value : i * 0.5);
            }

            return table;
        }

        var sequential = new MemoryStream();
        var parallel = new MemoryStream();
        ParquetDataWriter.Write(Table().CreateDataReader(), sequential, new ParquetDataWriterOptions { RowGroupSize = 1_000, MaxDegreeOfParallelism = 1 });
        ParquetDataWriter.Write(Table().CreateDataReader(), parallel, new ParquetDataWriterOptions { RowGroupSize = 1_000, MaxDegreeOfParallelism = 3 });

        await Assert.That(parallel.ToArray()).IsEquivalentTo(sequential.ToArray());
        await Assert.That(() => ParquetDataWriter.Write(Table().CreateDataReader(), new MemoryStream(), new ParquetDataWriterOptions { MaxDegreeOfParallelism = 0 })).Throws<ArgumentOutOfRangeException>();
    }

    // ---- the row group writer ----

    [Test]
    public async Task TheRowGroupWriter_RefusesAGroupThatHasStarted_AndAWrongNumberOfColumns()
    {
        var schema = Serializer<Sample>.Schema();
        using var writer = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = writer.AppendRowGroup();
        group.NextColumn();

        await Assert.That(() => group.WriteColumns([], 2)).Throws<InvalidOperationException>();
    }

    private static class Serializer<TRow>
    {
        public static ParquetNeo.Schema.ParquetSchema Schema() => ParquetSerializer.GetSchema<TRow>();
    }
}
