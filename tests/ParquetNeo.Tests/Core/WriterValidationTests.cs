using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class WriterValidationTests
{
    private const FieldRepetitionType Required = FieldRepetitionType.Required;
    private const FieldRepetitionType Optional = FieldRepetitionType.Optional;

    private static ParquetSchema TwoColumns() => new(
    [
        new PrimitiveNode("a", PhysicalType.Int32, Required),
        new PrimitiveNode("b", PhysicalType.Int32, Optional),
    ]);

    // ---- bad batches ----

    [Test]
    public async Task ABatchOfTheWrongType_IsRejected()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        var column = group.NextColumn();

        await Assert.That(() => column.WriteBatch([1L, 2L])).Throws<ArgumentException>();
        await Assert.That(() => column.WriteBatch([1.5])).Throws<ArgumentException>();
        await Assert.That(() => column.WriteBatch([true])).Throws<ArgumentException>();
        await Assert.That(() => column.WriteStrings(["x"])).Throws<ArgumentException>();
        await Assert.That(() => column.WriteFixedLengthByteArrays(new byte[4])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ARequiredColumn_TakesNoDefinitionLevels()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();

        await Assert.That(() => group.NextColumn().WriteBatch([1, 2], [1, 1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task AnOptionalColumn_NeedsOneLevelPerRow()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2, 3]);
        var optional = group.NextColumn();

        await Assert.That(() => optional.WriteBatch([1, 2, 3])).Throws<ArgumentException>(); // values but no levels
    }

    [Test]
    public async Task DefinitionLevels_MustMatchTheNumberOfValues_AndBeInRange()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2, 3]);
        var optional = group.NextColumn();

        await Assert.That(() => optional.WriteBatch([1, 2, 3], [1, 0, 1])).Throws<ArgumentException>(); // 2 present, 3 values
        await Assert.That(() => optional.WriteBatch([1], [1, 0, 1])).Throws<ArgumentException>(); // 2 present, 1 value
        await Assert.That(() => optional.WriteBatch([1, 2], [1, 2, 1])).Throws<ArgumentOutOfRangeException>(); // level above the maximum
        await Assert.That(() => optional.WriteBatch([1, 2], [1, -1, 1])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FixedLengthValues_MustBeAWholeNumberOfEntries()
    {
        var schema = new ParquetSchema([new PrimitiveNode("f", PhysicalType.FixedLenByteArray, Required, typeLength: 4)]);
        using var writer = new ParquetFileWriter(new MemoryStream(), schema);
        using var group = writer.AppendRowGroup();
        var column = group.NextColumn();

        await Assert.That(() => column.WriteFixedLengthByteArrays(new byte[10])).Throws<ArgumentException>();
    }

    [Test]
    public async Task EmptyBatches_AreHarmless()
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, TwoColumns(), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            var a = group.NextColumn();
            a.WriteBatch(ReadOnlySpan<int>.Empty);
            a.WriteBatch([1, 2]);
            a.WriteBatch(ReadOnlySpan<int>.Empty);
            var b = group.NextColumn();
            b.WriteBatch(ReadOnlySpan<int>.Empty, ReadOnlySpan<int>.Empty);
            b.WriteBatch([7], [1, 0]);
        }

        var file = TestParquetFile.Parse(stream.ToArray());
        file.AssertStructure();
        await Assert.That(file.ReadColumn<int>(0, 0).Select(v => v!.Value)).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(file.ReadColumn<int>(0, 1)).IsEquivalentTo(new int?[] { 7, null });
    }

    // ---- row group structure ----

    [Test]
    public async Task ColumnsWithDifferentRowCounts_AreRejected()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2, 3]);
        group.NextColumn().WriteBatch([1, 2], [1, 1]);

        await Assert.That(() => group.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ARowGroup_MustHaveEveryColumn()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2, 3]);

        await Assert.That(() => group.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NoMoreColumnsThanTheSchemaHas()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1]);
        group.NextColumn().WriteBatch([1], [1]);

        await Assert.That(() => group.NextColumn()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ARowGroup_NeedsAtLeastOneRow()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        var group = writer.AppendRowGroup();

        await Assert.That(() => group.NextColumn().WriteBatch(ReadOnlySpan<int>.Empty)).ThrowsNothing();
        await Assert.That(() => group.NextColumn()).Throws<InvalidOperationException>(); // completing the empty column fails
    }

    [Test]
    public async Task OnlyOneRowGroupCanBeOpenAtATime()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var first = writer.AppendRowGroup();

        await Assert.That(() => writer.AppendRowGroup()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AColumnWriter_CannotBeUsedAfterMovingOn()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        using var group = writer.AppendRowGroup();
        var first = group.NextColumn();
        first.WriteBatch([1, 2]);
        group.NextColumn();

        await Assert.That(() => first.WriteBatch([3])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ACompletedRowGroup_CannotBeReused()
    {
        var schema = new ParquetSchema([new PrimitiveNode("a", PhysicalType.Int32, Required)]);
        using var writer = new ParquetFileWriter(new MemoryStream(), schema);
        var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1]);
        group.Close();

        await Assert.That(() => group.NextColumn()).Throws<InvalidOperationException>();
        group.Close(); // closing again is allowed
    }

    // ---- the file as a whole ----

    [Test]
    public async Task ClosingTheFile_WhileAGroupIsOpen_IsRejected()
    {
        var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        writer.AppendRowGroup();

        await Assert.That(() => writer.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AnAbandonedRowGroup_StopsTheFileFromBeingCompleted_WithoutHidingTheOriginalError()
    {
        var stream = new MemoryStream();
        var writer = new ParquetFileWriter(stream, TwoColumns(), leaveOpen: true);

        // The caller's own exception happens while the group is open; disposing must not throw over it.
        Exception? original = null;
        try
        {
            using var group = writer.AppendRowGroup();
            group.NextColumn().WriteBatch([1, 2]);
            throw new FormatException("the caller's own problem");
        }
        catch (FormatException exception)
        {
            original = exception;
        }

        await Assert.That(original).IsNotNull();
        await Assert.That(() => writer.Close()).Throws<InvalidOperationException>();
        await Assert.That(() => writer.AppendRowGroup()).Throws<InvalidOperationException>();
        writer.Dispose(); // does not throw, and writes no footer
        await Assert.That(() => TestParquetFile.Parse(stream.ToArray())).Throws<InvalidDataException>(); // no footer was written
    }

    [Test]
    public async Task DisposingAGroupWhoseLastColumnHasNoValues_AbandonsTheFile_InsteadOfThrowing()
    {
        var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2]);
        group.NextColumn(); // started, but nothing written

        group.Dispose(); // must not throw

        await Assert.That(() => writer.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ClosingTheGroup_ReportsAColumnWithoutValues()
    {
        using var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        var group = writer.AppendRowGroup();
        group.NextColumn().WriteBatch([1, 2]);
        group.NextColumn();

        await Assert.That(() => group.Close()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UsingAClosedWriter_IsRejected()
    {
        var writer = new ParquetFileWriter(new MemoryStream(), TwoColumns());
        writer.Close();

        await Assert.That(() => writer.AppendRowGroup()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UnsupportedSchemas_AreRejectedWhenTheWriterIsCreated()
    {
        var int96 = new ParquetSchema([new PrimitiveNode("t", PhysicalType.Int96, Optional)]);
        var nestedInt96 = new ParquetSchema([new GroupNode("g", Optional, [new PrimitiveNode("t", PhysicalType.Int96, Optional)])]);

        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), int96)).Throws<NotSupportedException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), nestedInt96)).Throws<NotSupportedException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), new ParquetSchema([]))).Throws<ArgumentException>();
    }

    [Test]
    public async Task AnUnregisteredCodec_IsRejectedBeforeAnythingIsWritten()
    {
        var stream = new MemoryStream();
        var options = new ParquetWriterOptions { Compression = CompressionCodec.Snappy }; // an add-on, not registered by default

        var exception = Assert.Throws<NotSupportedException>(() => new ParquetFileWriter(stream, TwoColumns(), options));

        await Assert.That(exception.Message).Contains("ParquetNeo.Compression.Snappy");
        await Assert.That(stream.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task BadArguments_AreRejected()
    {
        var readOnly = new MemoryStream([], writable: false);

        await Assert.That(() => new ParquetFileWriter(null!, TwoColumns())).Throws<ArgumentNullException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ParquetFileWriter(readOnly, TwoColumns())).Throws<ArgumentException>();
        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), TwoColumns(), new ParquetWriterOptions { DataPageSize = 0 }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task NothingIsWrittenToTheStream_ButTheMagic_UntilDataArrives()
    {
        var stream = new MemoryStream();
        using var writer = new ParquetFileWriter(stream, TwoColumns(), leaveOpen: true);

        await Assert.That(stream.ToArray()).IsEquivalentTo("PAR1"u8.ToArray());
        await Assert.That(writer.Position).IsEqualTo(4L);
    }
}
