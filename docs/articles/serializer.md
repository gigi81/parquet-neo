# Serializing your classes

`ParquetSerializer` (package `ParquetNeo.Serialization`) writes the properties of your classes and records as columns and
reads them back.

```csharp
using ParquetNeo.Serialization;

ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions
{
    RowGroupSize = 100_000,        // rows in a row group: the unit that is read, skipped, and compressed together
    MaxDegreeOfParallelism = 4,    // columns of a row group are encoded on this many threads; the bytes do not depend on it
    Writer = new ParquetWriterOptions { Compression = CompressionCodec.Gzip },
});
```

## What maps to a column

Single columns: numbers, `decimal`, `bool`, strings, byte arrays, `Guid`, the date and time types (`DateOnly`, `TimeOnly`,
`DateTime`, `DateTimeOffset`), enums, and the nullable forms of those. Around them:

- **Nested objects and structs**, to any depth, become groups.
- **Arrays, lists, sets and other collections** become `LIST` columns, and **dictionaries** become `MAP`s.
- A property is optional (it may be null in the file) when its type is nullable: `int?`, `string?` with nullable reference types
  on, and so on.
- Records and classes with a constructor that has parameters are read by calling that constructor with the values of the
  properties it names.

The columns are matched with the properties by name when reading, so the file may have more columns than the class, and a column
that the class does not have is not read.

## Adjusting the mapping

Attributes in `ParquetNeo.DataModel` change how a property is stored:

| Attribute | Effect |
|---|---|
| `[ParquetColumn("name")]` | The name of the column, or its position among the columns |
| `[ParquetIgnore]` | The property is not written or read |
| `[ParquetDecimal(precision, scale)]` | The precision and scale of a `decimal` (the default is 38 and 18) |
| `[ParquetTime(unit)]`, `[ParquetTimestamp(...)]` | The unit and meaning of the column of a time or of a date and time |
| `[ParquetEncoding(encoding)]` | The encoding of the values of the column (`PLAIN` by default) |
| `[ParquetDictionary(mode)]` | Whether the column has a dictionary: on, off, or decided by what the values look like |

## Reading a big file

`Deserialize` returns a list. `DeserializeAll` and `DeserializeAllAsync` give the rows one row group at a time, so memory is
bounded by the largest row group:

```csharp
foreach (var row in ParquetSerializer.DeserializeAll<Person>(stream))
{
    // ...
}
```

To read less of the file, give a filter: see [Skipping what is not needed](filtering.md).
