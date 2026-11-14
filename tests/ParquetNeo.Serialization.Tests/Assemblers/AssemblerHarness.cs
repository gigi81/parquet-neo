using ParquetNeo.Schema;
using ParquetNeo.Serialization;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Tests.Assemblers;

/// <summary>
/// A file of rows written by the serializer, whose schema and columns the tests of the assemblers bind to and read from without
/// going through <see cref="ParquetSerializer.Deserialize{TRow}(Stream, ParquetDeserializerOptions?)"/>.
/// </summary>
internal sealed class AssemblerHarness : IDisposable
{
    private readonly ParquetFileReader _reader;

    private AssemblerHarness(byte[] bytes) => _reader = new ParquetFileReader(new MemoryStream(bytes));

    public static AssemblerHarness Write<TRow>(IEnumerable<TRow> rows, int rowGroupSize = int.MaxValue)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = rowGroupSize });
        return new AssemblerHarness(stream.ToArray());
    }

    public ParquetSchema Schema => _reader.Schema;

    /// <summary>The child of the root of the schema with this name, which is a column or a group.</summary>
    public SchemaNode Node(string name) => Schema.Root.Children.Single(child => child.Name == name);

    /// <summary>The leaf column of the file with this path, such as <c>Values.list.element</c>.</summary>
    public PrimitiveNode Column(string path) => Schema.Columns.Single(column => string.Join('.', column.Path) == path);

    /// <summary>Binds <paramref name="assembler"/> to <paramref name="node"/> and reads the first row group into the columns it needs.</summary>
    public ReadContext Read(Assembler assembler, SchemaNode? node, int columnCount, int nodeCount)
    {
        var binder = new ReadBinder(columnCount, nodeCount);
        assembler.Bind(node, binder);
        return Fill(assembler, binder);
    }

    /// <summary>Binds the root assembler of a row type to the root of the schema and reads the first row group.</summary>
    public ReadContext Read<TRow>(AssemblerBuilder builder, StructAssembler<TRow> row)
    {
        var binder = new ReadBinder(builder.SlotCount, builder.NodeCount);
        row.BindRow(Schema.Root, binder);
        return Fill(row, binder);
    }

    private ReadContext Fill(Assembler assembler, ReadBinder binder)
    {
        var plan = binder.ToPlan();
        var columns = new List<ILeafColumn>();
        var objects = new List<object>();
        assembler.AddState(columns, objects);
        var group = _reader.OpenRowGroup(0);
        for (int slot = 0; slot < columns.Count; slot++)
        {
            if (plan.FileColumns[slot] < 0)
            {
                columns[slot].Fill(null);
            }
            else
            {
                using var column = group.OpenColumn(plan.FileColumns[slot]);
                columns[slot].Fill(column);
            }
        }

        return new ReadContext(columns.ToArray(), objects.ToArray(), plan.Nodes);
    }

    public void Dispose() => _reader.Dispose();
}
