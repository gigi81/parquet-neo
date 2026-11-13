using System.Runtime.CompilerServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;
using ParquetNeo.Serialization.Assemblers;
using ParquetNeo.Serialization.Buffers;
using ParquetNeo.Serialization.Columns;
using ParquetNeo.Serialization.Shredders;

namespace ParquetNeo.AotSmoke;

/// <summary>
/// The mapping of <see cref="Order"/> written by hand, in the form the source generator will write it: nothing is found by
/// reflection and no code is compiled at run time, so it works with Native AOT. It is also used by the tests, which compare what
/// it writes and reads with what the reflection path does for the same type.
/// </summary>
public static class OrderMapping
{
    private const string Id = "Property 'Order.Id'";
    private const string Name = "Property 'Order.Name'";
    private const string Score = "Property 'Order.Score'";
    private const string Status = "Property 'Order.Status'";
    private const string Quantity = "Property 'Order.Quantity'";
    private const string RowDescription = "Row type 'Order'";

    /// <summary>Makes the serializer use this mapping for <see cref="Order"/> instead of looking at the type.</summary>
    public static void Register()
    {
        // With Native AOT the family of enums cannot be closed at run time, so the enum is made known here.
        ColumnTypeRegistry.RegisterEnum<OrderStatus>();
        RowModel<Order>.Register(CreateRowModel());
        ReadModel<Order>.Register(CreateReadModel());
    }

    private static RowModel<Order> CreateRowModel()
    {
        var id = ColumnTypeRegistry.Get<long>();
        var name = ColumnTypeRegistry.Get<string>();
        var score = ColumnTypeRegistry.Get<double>();
        var status = ColumnTypeRegistry.Get<OrderStatus>();
        var quantity = ColumnTypeRegistry.Get<int>();

        var idShredder = new LeafShredder<long>(0, false, id, Id);
        var nameShredder = new LeafShredder<string>(1, true, name, Name);
        var scoreShredder = new LeafShredder<double>(2, false, score, Score);
        var statusShredder = new LeafShredder<OrderStatus>(3, false, status, Status);
        var quantityShredder = new LeafShredder<int>(4, true, quantity, Quantity);

        PropertyShredder[] properties =
        [
            new PropertyShredder<long>(nameof(Order.Id), idShredder, null, nullable: false),
            new PropertyShredder<string>(nameof(Order.Name), nameShredder, null, nullable: false),
            new PropertyShredder<double>(nameof(Order.Score), scoreShredder, null, nullable: false),
            new PropertyShredder<OrderStatus>(nameof(Order.Status), statusShredder, null, nullable: false),
            new PropertyShredder<int>(nameof(Order.Quantity), quantityShredder, null, nullable: true),
        ];

        // Straight-line code: each property goes to the buffer of its column, a null of an optional one as a missing entry.
        static void Shred(Order row, int definition, int repetition, ILeafBuffer[] buffers)
        {
            Unsafe.As<LeafBuffer<long>>(buffers[0]).AddValue(row.Id, definition, repetition);

            if (row.Name is { } text)
            {
                Unsafe.As<LeafBuffer<string>>(buffers[1]).AddValue(text, definition + 1, repetition);
            }
            else
            {
                buffers[1].AddMissing(definition, repetition);
            }

            Unsafe.As<LeafBuffer<double>>(buffers[2]).AddValue(row.Score, definition, repetition);
            Unsafe.As<LeafBuffer<OrderStatus>>(buffers[3]).AddValue(row.Status, definition, repetition);

            if (row.Quantity is { } count)
            {
                Unsafe.As<LeafBuffer<int>>(buffers[4]).AddValue(count, definition + 1, repetition);
            }
            else
            {
                buffers[4].AddMissing(definition, repetition);
            }
        }

        LeafInfo[] columns =
        [
            new(nameof(Order.Id), null, null, capacity => new LeafBuffer<long>(false, false, id, capacity)),
            new(nameof(Order.Name), null, null, capacity => new LeafBuffer<string>(true, false, name, capacity)),
            new(nameof(Order.Score), null, null, capacity => new LeafBuffer<double>(false, false, score, capacity)),
            new(nameof(Order.Status), null, null, capacity => new LeafBuffer<OrderStatus>(false, false, status, capacity)),
            new(nameof(Order.Quantity), null, null, capacity => new LeafBuffer<int>(true, false, quantity, capacity)),
        ];

        return new RowModel<Order>(new StructShredder<Order>(false, 0, columns.Length, RowDescription, properties, Shred), columns);
    }

    private static ReadModel<Order> CreateReadModel()
    {
        var idAssembler = new LeafAssembler<long>(0, 0, Id);
        var nameAssembler = new LeafAssembler<string>(1, 0, Name);
        var scoreAssembler = new LeafAssembler<double>(2, 0, Score);
        var statusAssembler = new LeafAssembler<OrderStatus>(3, 0, Status);
        var quantityAssembler = new LeafAssembler<int>(4, 0, Quantity);

        IPropertyAssembler[] properties =
        [
            new PropertyAssembler<long>(nameof(Order.Id), Id, false, 0, idAssembler, null),
            new PropertyAssembler<string>(nameof(Order.Name), Name, true, 1, nameAssembler, null),
            new PropertyAssembler<double>(nameof(Order.Score), Score, false, 2, scoreAssembler, null),
            new PropertyAssembler<OrderStatus>(nameof(Order.Status), Status, false, 3, statusAssembler, null),
            new PropertyAssembler<int>(nameof(Order.Quantity), Quantity, true, 4, quantityAssembler, null),
        ];

        // Straight-line code: the next value of each column goes to its property; a column without one is a null.
        static Order Build(ReadContext context)
        {
            var row = new Order();
            var columns = context.Columns;

            row.Id = Unsafe.As<LeafColumn<long>>(columns[0]).TryTake(out long id) ? id : throw Null(Id, context);
            row.Name = Unsafe.As<LeafColumn<string>>(columns[1]).TryTake(out string? name) ? name : null;
            row.Score = Unsafe.As<LeafColumn<double>>(columns[2]).TryTake(out double score) ? score : throw Null(Score, context);
            row.Status = Unsafe.As<LeafColumn<OrderStatus>>(columns[3]).TryTake(out OrderStatus status) ? status : throw Null(Status, context);
            row.Quantity = Unsafe.As<LeafColumn<int>>(columns[4]).TryTake(out int quantity) ? quantity : null;
            return row;
        }

        var root = new StructAssembler<Order>(0, -1, false, 0, 5, RowDescription, properties, null, Build);
        return new ReadModel<Order>(root, 5, 1);
    }

    private static ValueConversionException Null(string description, ReadContext context) =>
        new($"{description} cannot be null, but row {context.Row} of the file has a null for it.");
}
