# Skipping what is not needed

The fastest way to read a big file is to not read most of it. A file has several levels of information that tell a reader what a part
of it holds, and a `ParquetFilter` is checked against them:

| Level | What it records | Written by default | Rules out |
|---|---|---|---|
| Row group statistics | the smallest and largest value of each column of the row group, and how many are null | yes | whole row groups |
| Page index | the same for every page of a column, and where the page is | no (`WritePageIndex`) | pages inside a row group |
| Bloom filters | whether a value is *certainly not* in the column of a row group | no (`ColumnBloomFilters`) | row groups, for equality conditions |
| Page checksums | a CRC-32 of every page | no (`WritePageChecksums`) | nothing: they detect damaged pages |

## Filters

```csharp
using ParquetNeo;

var recent = ParquetFilter.Column("Born").GreaterThanOrEqual(new DateOnly(1900, 1, 1))
           & ParquetFilter.Column("Name").IsNotNull();
```

A filter is made of comparisons (`Equal`, `NotEqual`, `GreaterThan`, ..., `Between`, `In`), null tests, and `And` (`&`), `Or`
(`|`) and `Not` (`!`).

> [!IMPORTANT]
> A filter only **prunes**. It rules out what cannot match, and the rows of what it keeps are all returned, including rows that do
> not satisfy it. Apply the condition to the rows as well when the result must be exact. (`ParquetNeo.Linq` does that for you.)

## Using them

```csharp
var rows = ParquetSerializer.Deserialize<Person>(stream, new ParquetDeserializerOptions { RowGroupFilter = recent });
```

`ParquetDataReaderOptions.RowGroupFilter` is the same for the ADO.NET reader, and `ParquetFileReader.GetMatchingRowGroups(filter)`
tells you which row groups of a file may match.

The row groups that are ruled out are not read. The asynchronous methods of `ParquetSerializer` do not even fetch their bytes, but
they only use the statistics: they do not fetch the page index or the bloom filters.

## The page index

A row group is made of many pages, and its statistics say nothing of which of them hold a value. With a page index, a filter skips
pages as well as row groups:

```csharp
var options = new ParquetSerializerOptions { Writer = new ParquetWriterOptions { WritePageIndex = true } };
```

It pays when the values of a column are in some order (a time, an id that grows), which makes the bounds of a page tight. Reading
100 ids out of 2 million in one row group took 313 ms with the statistics of the row group only and 39 ms with the page index
(158 and 48 ms with gzip). The index took 2.5 KB of a 63 MB file, and writing took 8 to 10% longer.

At the low level, `RowGroupReader.SelectRows(filter, columns)` returns the rows to read as a `RowSelection`, and
`OpenColumn(index, selection)` reads only those rows: the pages with none of them are not read.

## Bloom filters

Statistics rule out little when values are spread widely (ids, names, e-mail addresses): every row group has a smallest and a
largest value that bracket what you look for. A bloom filter says that a value is certainly not in a row group, or that it may be
(with a small chance of a wrong "may be", the false positive rate).

```csharp
var written = new ParquetSerializerOptions
{
    Writer = new ParquetWriterOptions
    {
        ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["Email"] = new() { FalsePositiveRate = 0.01 } },
    },
};

var found = ParquetSerializer.Deserialize<Person>(stream, new ParquetDeserializerOptions
{
    RowGroupFilter = ParquetFilter.Column("Email").Equal("ada@example.com"),
});
```

A filter takes room in the file: about a byte for every few different values. Writing the column takes several times as long when
its pages are not compressed, and a little longer when they are. Bloom filters in files written by other tools are read too.

## Page checksums

```csharp
var written = new ParquetWriterOptions { WritePageChecksums = true };
var read = new ParquetReaderOptions { VerifyPageChecksums = true };   // a damaged page throws CorruptColumnException
```

Computing the checksum costs about a second per GB of page bytes.
