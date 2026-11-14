# Trimming and Native AOT

`ParquetNeo` and `ParquetNeo.Serialization` are trim and Native AOT safe (`IsAotCompatible`). `ParquetNeo.Linq` is not: it builds
expression trees and types at run time.

By default the serializer finds out how a type maps to columns by reflection and compiles that into code the first time the type
is used, which neither trimming nor Native AOT allows. The source generator, which comes with the `ParquetNeo.Serialization`
package, writes the mapping when you build, so nothing is looked at or compiled at run time.

```csharp
using ParquetNeo.DataModel;

[ParquetSerializable]
public sealed class Order
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public double Total { get; set; }
}
```

For a type you do not own, put the attribute on the assembly:

```csharp
[assembly: ParquetSerializable(typeof(TheirType))]
```

The serializer is called as before, and uses the generated mapping when there is one.

## What the generator handles

Properties that are single columns (numbers, strings, dates, `Guid`, enums, byte arrays and their nullable forms), objects and
structs nested to any depth, and arrays, lists, sets and dictionaries of any of them. A type created with a constructor without
parameters is read with the setters of its properties; one without, such as a record, is read by calling its constructor with the
values of the properties it names.

## What it does when it cannot

For a type it cannot map (a property of a type that is not stored as columns, or no constructor that fits) the generator reports a
warning at build time, and the type keeps working through reflection. A type it can write but not read gets a warning of its own.
In a Native AOT program a type with no generated mapping is refused, with an explanation of why.

## Checking it

The repository publishes a small program with Native AOT in its CI, and checks the packages by installing `ParquetNeo.All` in a new
program published the same way.
